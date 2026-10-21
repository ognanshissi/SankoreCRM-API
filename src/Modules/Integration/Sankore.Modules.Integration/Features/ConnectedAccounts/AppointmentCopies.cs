namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Security.Cryptography;
using System.Text;
using Hangfire;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Copies SANKORÉ rendez-vous into the target agenda of EACH person concerned — the organiser and
/// every invited colleague who chose one (connected agendas, lot 4). M13 stays the reference: a
/// copy follows the appointment's moves and goes when it is cancelled; what a user changes in
/// Outlook or Google is not read back.
///
/// <para>
/// <b>One rule, two entry points.</b> For a (rendez-vous, person) pair the wanted state is « a
/// copy in their CURRENT target agenda, with the appointment's current content » when the
/// appointment is live and they have a target, « no copy » otherwise. An appointment that changed
/// reconciles everybody concerned; a person who chose another target reconciles their upcoming
/// appointments. Both read the appointment from M13 as it stands, never from a message, so changes
/// arriving out of order cannot win over the latest state.
/// </para>
///
/// <para>
/// <b>What leaves the institution</b> is the title, the times, the place or the meeting link —
/// never the description, the lead or the client (<see cref="AppointmentCalendarEntry"/>).
/// </para>
///
/// <para>
/// A provider failure is RECORDED on the copy row, never thrown: the 15-minute job retries failed
/// copies (<see cref="AppointmentCalendarCopy.MaxAttempts"/> in a row at most), and a later change
/// of the appointment tries again anyway.
/// </para>
/// </summary>
internal sealed class AppointmentCalendarCopies(
    IntegrationDbContext db,
    ILeadsModule leads,
    ConnectedAccountProviders providers,
    ConnectedAccountTokens tokens,
    TimeProvider clock,
    ILogger<AppointmentCalendarCopies> logger)
{
    private sealed record Target(ConnectedCalendar Calendar, ConnectedAccount Account);

    /// <summary>
    /// A target anywhere in the tenant, or a copy of this appointment left to delete — the
    /// consumer's cheap way out.
    /// </summary>
    public async Task<bool> AnyInUseAsync(Guid appointmentId, CancellationToken ct) =>
        await db.ConnectedCalendars.AnyAsync(c => c.IsAppointmentTarget, ct)
        || await db.AppointmentCalendarCopies.AnyAsync(c => c.AppointmentId == appointmentId, ct);

    public async Task ReconcileAppointmentAsync(Guid tenantId, Guid appointmentId, CancellationToken ct)
    {
        var entry = await leads.GetAppointmentForCalendarAsync(tenantId, appointmentId, ct);
        var copies = await db.AppointmentCalendarCopies.AsTracking().Where(c => c.AppointmentId == appointmentId).ToListAsync(ct);

        var users = Attendees(entry).Union(copies.Select(c => c.UserId)).ToList();
        if (users.Count == 0) return;

        var targets = await TargetsAsync(users, ct);

        foreach (var userId in users)
        {
            await ReconcileOneAsync(tenantId, appointmentId, entry, userId,
                copies.FirstOrDefault(c => c.UserId == userId), targets.GetValueOrDefault(userId), ct);
        }
    }

    /// <summary>
    /// A person chose, changed or dropped their target: their UPCOMING rendez-vous follow. Copies of
    /// past ones stay where they are — they are history, not a plan.
    /// </summary>
    public async Task ReconcileUserAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var upcoming = await leads.ListAppointmentsForUserAsync(tenantId, userId, now, now + ConnectedCalendar.WindowAhead, ct);
        var copies = await db.AppointmentCalendarCopies.AsTracking().Where(c => c.UserId == userId).ToListAsync(ct);
        var target = (await TargetsAsync([userId], ct)).GetValueOrDefault(userId);

        foreach (var entry in upcoming)
            await ReconcileOneAsync(tenantId, entry.Id, entry, userId, copies.FirstOrDefault(c => c.AppointmentId == entry.Id), target, ct);
    }

    /// <summary>The 15-minute job: every appointment with a failed copy, once more.</summary>
    public async Task RetryFailedAsync(Guid tenantId, CancellationToken ct)
    {
        var appointmentIds = await db.AppointmentCalendarCopies
            .Where(c => c.Status == AppointmentCopyStatus.Failed && c.Attempts < AppointmentCalendarCopy.MaxAttempts)
            .Select(c => c.AppointmentId)
            .Distinct()
            .Take(200)
            .ToListAsync(ct);

        foreach (var id in appointmentIds)
        {
            try
            {
                await ReconcileAppointmentAsync(tenantId, id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Appointment {AppointmentId}: retrying its agenda copies failed.", id);
            }
        }
    }

    /// <summary>
    /// « Retirer » an account: what SANKORÉ wrote into it goes with it — the copies are deleted at
    /// the provider (best effort, while the token still works) and forgotten. Called BEFORE the
    /// tokens are revoked.
    /// </summary>
    public async Task ForgetAccountAsync(ConnectedAccount account, CancellationToken ct)
    {
        var copies = await db.AppointmentCalendarCopies.AsTracking().Where(c => c.AccountId == account.Id).ToListAsync(ct);
        if (copies.Count == 0) return;

        var calendars = await db.ConnectedCalendars.Where(c => c.AccountId == account.Id).ToDictionaryAsync(c => c.Id, ct);
        var provider = providers.Find(account.Provider);
        var token = provider is null ? null : await tokens.GetAccessTokenAsync(account, ct);

        if (provider is not null && token is { IsSuccess: true })
        {
            foreach (var copy in copies.Where(c => c.ExternalEventId is not null && calendars.ContainsKey(c.CalendarId)))
            {
                try
                {
                    await provider.DeleteEventAsync(token.Value!, calendars[copy.CalendarId].ExternalCalendarId, copy.ExternalEventId!, ct);
                }
                catch (ProviderAuthException ex)
                {
                    logger.LogInformation("Copy {CopyId}: not deleted at the provider on account removal ({Code}).", copy.Id, ex.Code);
                }
            }
        }

        db.AppointmentCalendarCopies.RemoveRange(copies);
    }

    // ── The rule ─────────────────────────────────────────────────────────

    private async Task ReconcileOneAsync(
        Guid tenantId, Guid appointmentId, AppointmentCalendarEntry? entry, Guid userId,
        AppointmentCalendarCopy? copy, Target? target, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var live = entry is { IsCancelled: false } && Attendees(entry).Contains(userId);

        // In the wrong place, or no longer wanted: delete it first. A deletion that fails keeps the
        // row, so the copy is not forgotten while it still exists somewhere.
        if (copy is not null && (!live || target is null || copy.CalendarId != target.Calendar.Id))
        {
            if (!await DeleteAsync(copy, now, ct))
            {
                await db.SaveChangesAsync(ct);
                return;
            }

            db.AppointmentCalendarCopies.Remove(copy);
            copy = null;
        }

        if (!live || target is null)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        if (copy is null)
        {
            // Nothing to plan any more: an appointment already over is not copied after the fact.
            if (entry!.EndAt <= now)
            {
                await db.SaveChangesAsync(ct);
                return;
            }

            copy = AppointmentCalendarCopy.Start(tenantId, appointmentId, target.Calendar, now);
            db.AppointmentCalendarCopies.Add(copy);
        }

        await WriteAsync(copy, entry!, target, now, ct);

        // Saved per person: an event created at the provider and never recorded here would be
        // created a second time by the next attempt.
        await db.SaveChangesAsync(ct);
    }

    private async Task WriteAsync(AppointmentCalendarCopy copy, AppointmentCalendarEntry entry, Target target, DateTimeOffset now, CancellationToken ct)
    {
        var draft = Draft(entry);
        var hash = Hash(draft);

        if (copy.Status == AppointmentCopyStatus.Synced && copy.ExternalEventId is not null && copy.ContentHash == hash) return;

        var provider = providers.Find(target.Account.Provider);
        if (provider is null)
        {
            copy.RecordFailure(ConnectedAccountErrors.ProviderUnavailable, now);
            return;
        }

        var token = await tokens.GetAccessTokenAsync(target.Account, ct);
        if (token.IsFailure)
        {
            copy.RecordFailure(token.Error!, now);
            return;
        }

        try
        {
            if (copy.ExternalEventId is { } existing
                && await provider.UpdateEventAsync(token.Value!, target.Calendar.ExternalCalendarId, existing, draft, ct))
            {
                copy.RecordSynced(existing, hash, now);
                return;
            }

            copy.ForgetExternalEvent();
            var created = await provider.CreateEventAsync(token.Value!, target.Calendar.ExternalCalendarId, draft, ct);
            copy.RecordSynced(created, hash, now);
        }
        catch (ProviderAuthException ex)
        {
            logger.LogWarning("Appointment {AppointmentId}: copy for user {UserId} failed ({Code}).", copy.AppointmentId, copy.UserId, ex.Code);
            copy.RecordFailure(ex.Code, now);
        }
    }

    private async Task<bool> DeleteAsync(AppointmentCalendarCopy copy, DateTimeOffset now, CancellationToken ct)
    {
        if (copy.ExternalEventId is null) return true;

        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == copy.AccountId, ct);
        var calendar = await db.ConnectedCalendars.FirstOrDefaultAsync(c => c.Id == copy.CalendarId, ct);

        // The account or the agenda is gone, and the copy with it.
        if (account is null || calendar is null) return true;

        var provider = providers.Find(account.Provider);
        if (provider is null)
        {
            copy.RecordFailure(ConnectedAccountErrors.ProviderUnavailable, now);
            return false;
        }

        var token = await tokens.GetAccessTokenAsync(account, ct);
        if (token.IsFailure)
        {
            copy.RecordFailure(token.Error!, now);
            return false;
        }

        try
        {
            await provider.DeleteEventAsync(token.Value!, calendar.ExternalCalendarId, copy.ExternalEventId, ct);
            return true;
        }
        catch (ProviderAuthException ex)
        {
            copy.RecordFailure(ex.Code, now);
            return false;
        }
    }

    private async Task<Dictionary<Guid, Target>> TargetsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await (
                from c in db.ConnectedCalendars.AsTracking()
                join a in db.ConnectedAccounts.AsTracking() on c.AccountId equals a.Id
                where c.IsAppointmentTarget && userIds.Contains(c.UserId)
                select new { Calendar = c, Account = a })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.Calendar.UserId).ToDictionary(g => g.Key, g => new Target(g.First().Calendar, g.First().Account));
    }

    private static IEnumerable<Guid> Attendees(AppointmentCalendarEntry? entry) =>
        entry is null ? [] : entry.ParticipantIds.Append(entry.OrganizerId).Where(id => id != Guid.Empty).Distinct();

    internal static ProviderEventDraft Draft(AppointmentCalendarEntry entry)
    {
        var body = new StringBuilder("Rendez-vous planifié dans SANKORÉ.");
        if (!string.IsNullOrWhiteSpace(entry.MeetingUrl)) body.Append("\n\nLien de la réunion : ").Append(entry.MeetingUrl);
        body.Append("\n\nPour le déplacer ou l’annuler, passez par SANKORÉ : cette copie suit le rendez-vous, l’inverse n’est pas vrai.");

        var location = !string.IsNullOrWhiteSpace(entry.Location)
            ? entry.Location
            : !string.IsNullOrWhiteSpace(entry.MeetingUrl) ? "En ligne" : null;

        return new ProviderEventDraft(entry.Title, body.ToString(), location, entry.StartAt, entry.EndAt);
    }

    internal static string Hash(ProviderEventDraft d) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{d.Title}\n{d.Body}\n{d.Location}\n{d.StartAt.UtcTicks}\n{d.EndAt.UtcTicks}")));
}

// ── Triggers ─────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// M13 planned, moved or cancelled a rendez-vous. One message at a time
/// (<see cref="AppointmentCalendarCopyConsumerDefinition"/>): two changes of the same appointment
/// handled in parallel would both find no copy and both create one.
/// </summary>
public sealed class AppointmentCalendarCopyConsumer(IServiceScopeFactory scopeFactory)
    : IConsumer<AppointmentChangedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<AppointmentChangedIntegrationEvent> context)
    {
        var (tenantId, appointmentId) = (context.Message.TenantId, context.Message.AppointmentId);
        if (tenantId == Guid.Empty) return;

        // The ambient scope first: ITenantContext is built from it, and a consumer has no request.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var copies = scope.ServiceProvider.GetRequiredService<AppointmentCalendarCopies>();

        // Most tenants never choose a target: no call to M13 for them.
        if (!await copies.AnyInUseAsync(appointmentId, context.CancellationToken)) return;

        await copies.ReconcileAppointmentAsync(tenantId, appointmentId, context.CancellationToken);
    }
}

public sealed class AppointmentCalendarCopyConsumerDefinition : ConsumerDefinition<AppointmentCalendarCopyConsumer>
{
    public AppointmentCalendarCopyConsumerDefinition() => ConcurrentMessageLimit = 1;
}

/// <summary>A person chose another target agenda: their upcoming rendez-vous move with it.</summary>
public sealed class ReconcileUserAppointmentCopiesJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId, Guid userId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppointmentCalendarCopies>().ReconcileUserAsync(tenantId, userId, CancellationToken.None);
    }
}

// ── Choosing the target ──────────────────────────────────────────────────────────────────────

/// <param name="CalendarId">One of the caller's writable agendas, or null to stop copying.</param>
public sealed record SetAppointmentTargetRequest(Guid? CalendarId);

internal sealed record SetAppointmentTargetCommand(Guid? CalendarId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedCalendar";
    public string? ResourceId => CalendarId?.ToString();
}

/// <summary>
/// Picks the agenda the caller's rendez-vous are copied into (or none). The copies themselves are
/// moved in the background — a user with fifty upcoming appointments is not kept waiting on fifty
/// provider calls.
/// </summary>
internal sealed class SetAppointmentTargetHandler(IntegrationDbContext db, ICurrentUser me, IBackgroundJobClient jobs)
    : IRequestHandler<SetAppointmentTargetCommand, Result>
{
    public async Task<Result> Handle(SetAppointmentTargetCommand cmd, CancellationToken ct)
    {
        ConnectedCalendar? chosen = null;

        if (cmd.CalendarId is { } calendarId)
        {
            chosen = await db.ConnectedCalendars.AsTracking().FirstOrDefaultAsync(c => c.Id == calendarId && c.UserId == me.Id, ct);
            if (chosen is null) return Result.Fail(ConnectedAccountErrors.NotFound);
            if (chosen.IsAppointmentTarget) return Result.Ok();
            if (chosen.IsReadOnly) return Result.Fail("CALENDAR_READ_ONLY");
        }

        var current = await db.ConnectedCalendars.AsTracking().Where(c => c.UserId == me.Id && c.IsAppointmentTarget).ToListAsync(ct);
        if (chosen is null && current.Count == 0) return Result.Ok();

        // Cleared and saved BEFORE the new one is set: the unique index is checked row by row, and
        // two targets for an instant would be refused.
        foreach (var c in current) c.ClearAppointmentTarget();
        await db.SaveChangesAsync(ct);

        if (chosen is not null)
        {
            var made = chosen.MakeAppointmentTarget();
            if (made.IsFailure) return made;
            await db.SaveChangesAsync(ct);
        }

        var (tenantId, userId) = (me.TenantId, me.Id);
        jobs.Enqueue<ReconcileUserAppointmentCopiesJob>(job => job.ExecuteAsync(tenantId, userId));
        return Result.Ok();
    }
}
