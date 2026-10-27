namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Encrypts what an event says — title and location — with the Integration field key. The slot
/// itself (start, end, busy) stays in clear: lot 6 shows it to colleagues as « Occupé ».
/// </summary>
internal sealed class CalendarEventProtector(
    [FromKeyedServices(Infrastructure.Crypto.IntegrationFieldProtection.Key)] IFieldEncryptor encryptor)
{
    public string? Protect(string? value) => string.IsNullOrWhiteSpace(value) ? null : encryptor.Encrypt(value.Trim());

    /// <summary>Null on ciphertext that will not open: one broken event must not empty an agenda.</summary>
    public string? Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        try
        {
            return encryptor.Decrypt(stored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }
}

/// <summary>What one account's sync did.</summary>
internal sealed record CalendarSyncOutcome(int Calendars, int Upserted, int Removed, IReadOnlyList<string> Failures);

/// <summary>
/// Brings a connected account's agendas up to date (connected agendas, lot 2): its list of agendas
/// first, then each TICKED agenda's events inside the window.
///
/// <para>
/// <b>A full read sweeps, an incremental one patches.</b> When an agenda has no usable cursor — first
/// read, a window more than a week old, a cursor the provider forgot — every event of the window is
/// read and what was stored but not seen again is deleted. Otherwise only the provider's changes
/// and removals are applied. An incremental Google read is not limited to the window, so whatever
/// falls outside it is dropped rather than stored.
/// </para>
///
/// <para>
/// <b>One agenda failing does not stop the others</b>, and an account the provider stopped trusting
/// is not read at all — <see cref="ConnectedAccountTokens"/> marks it « à reconnecter ».
/// </para>
/// </summary>
internal sealed class ConnectedCalendarSync(
    IntegrationDbContext db,
    ConnectedAccountTokens tokens,
    ConnectedAccountProviders providers,
    CalendarEventProtector protector,
    TimeProvider clock,
    ILogger<ConnectedCalendarSync> logger)
{
    /// <summary>Pages per agenda per run: a runaway cursor must not hold a worker.</summary>
    internal const int MaxPages = 50;

    public async Task<Result<CalendarSyncOutcome>> SyncAccountAsync(ConnectedAccount account, CancellationToken ct)
    {
        // An IMAP mailbox has no agenda to read; its mail is synced elsewhere.
        if (!account.HasCalendars) return Result.Ok(new CalendarSyncOutcome(0, 0, 0, []));

        var provider = providers.Find(account.Provider);
        if (provider is null) return Result.Fail<CalendarSyncOutcome>(ConnectedAccountErrors.ProviderUnavailable);

        var token = await tokens.GetAccessTokenAsync(account, ct);
        if (token.IsFailure) return Result.Fail<CalendarSyncOutcome>(token.Error!);

        IReadOnlyList<ProviderCalendar> listed;

        try
        {
            listed = await provider.ListCalendarsAsync(token.Value!, ct);
        }
        catch (ProviderAuthException ex)
        {
            if (ex.Revoked) account.MarkNeedsReconnect(ex.Code);
            await db.SaveChangesAsync(ct);
            return Result.Fail<CalendarSyncOutcome>(ex.Revoked ? ConnectedAccountErrors.NeedsReconnect : ConnectedAccountErrors.ProviderError);
        }

        var calendars = await ReconcileCalendarsAsync(account, listed, ct);

        var upserted = 0;
        var removed = 0;
        var failures = new List<string>();

        foreach (var calendar in calendars.Where(c => c.IsSelected))
        {
            try
            {
                var (u, r) = await SyncCalendarAsync(provider, token.Value!, calendar, ct);
                upserted += u;
                removed += r;
            }
            catch (ProviderAuthException ex)
            {
                logger.LogWarning("Connected agenda {CalendarId}: read failed ({Code}).", calendar.Id, ex.Code);
                calendar.RecordFailure(ex.Code);
                failures.Add(calendar.Name);
            }

            await db.SaveChangesAsync(ct);
        }

        account.MarkSynced(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CalendarSyncOutcome(calendars.Count, upserted, removed, failures));
    }

    /// <summary>
    /// The account's agendas as the provider lists them now: new ones appear (ticked), existing
    /// ones are renamed or recoloured, and one the provider no longer lists goes with its events.
    /// </summary>
    private async Task<List<ConnectedCalendar>> ReconcileCalendarsAsync(
        ConnectedAccount account, IReadOnlyList<ProviderCalendar> listed, CancellationToken ct)
    {
        var stored = await db.ConnectedCalendars.AsTracking().Where(c => c.AccountId == account.Id).ToListAsync(ct);
        var result = new List<ConnectedCalendar>();

        foreach (var item in listed)
        {
            var calendar = stored.FirstOrDefault(c => c.ExternalCalendarId == item.Id);

            if (calendar is null)
            {
                calendar = ConnectedCalendar.Discover(
                    account.TenantId, account.Id, account.UserId, item.Id, item.Name, item.Color, item.IsPrimary, item.IsReadOnly, item.IsSubscription);
                db.ConnectedCalendars.Add(calendar);
            }
            else
            {
                calendar.Refresh(item.Name, item.Color, item.IsPrimary, item.IsReadOnly, item.IsSubscription);
            }

            result.Add(calendar);
        }

        var gone = stored.Where(c => listed.All(l => l.Id != c.ExternalCalendarId)).ToList();

        if (gone.Count > 0)
        {
            var goneIds = gone.Select(c => c.Id).ToList();
            db.ConnectedCalendarEvents.RemoveRange(await db.ConnectedCalendarEvents.AsTracking().Where(e => goneIds.Contains(e.CalendarId)).ToListAsync(ct));
            // The rendez-vous copied into it went with the agenda (lot 4): forget where they were.
            db.AppointmentCalendarCopies.RemoveRange(await db.AppointmentCalendarCopies.AsTracking().Where(c => goneIds.Contains(c.CalendarId)).ToListAsync(ct));
            db.ConnectedCalendars.RemoveRange(gone);
        }

        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<(int Upserted, int Removed)> SyncCalendarAsync(
        IConnectedAccountProvider provider, string accessToken, ConnectedCalendar calendar, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var full = calendar.NeedsFullSync(now);

        var windowStart = full ? now - ConnectedCalendar.WindowBack : calendar.WindowStart!.Value;
        var windowEnd = windowStart + ConnectedCalendar.WindowBack + ConnectedCalendar.WindowAhead;

        var existing = await db.ConnectedCalendarEvents.AsTracking()
            .Where(e => e.CalendarId == calendar.Id)
            .ToDictionaryAsync(e => e.ExternalEventId, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var upserted = 0;
        var removed = 0;
        var cursor = full ? null : calendar.SyncCursor;
        string? delta = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var result = await provider.ReadEventsAsync(accessToken, calendar.ExternalCalendarId, cursor, windowStart, windowEnd, ct);

            if (result.CursorExpired)
            {
                if (cursor is null) throw new ProviderAuthException("cursor_expired_on_full_read", revoked: false);

                // Start the window again, from scratch, in this same run.
                calendar.ResetCursor();
                return await SyncCalendarAsync(provider, accessToken, calendar, ct);
            }

            foreach (var item in result.Changed)
            {
                seen.Add(item.Id);

                // Outside the window — possible on an incremental Google read: not stored.
                if (item.EndAt <= windowStart || item.StartAt >= windowEnd)
                {
                    if (existing.Remove(item.Id, out var outside))
                    {
                        db.ConnectedCalendarEvents.Remove(outside);
                        removed++;
                    }

                    continue;
                }

                if (!existing.TryGetValue(item.Id, out var stored))
                {
                    stored = ConnectedCalendarEvent.Create(calendar.TenantId, calendar, item.Id, now);
                    db.ConnectedCalendarEvents.Add(stored);
                    existing[item.Id] = stored;
                }

                stored.Apply(item.StartAt, item.EndAt, item.IsAllDay, item.ShowAs,
                    protector.Protect(item.Title), protector.Protect(item.Location), now);
                upserted++;
            }

            foreach (var id in result.Removed)
            {
                if (existing.Remove(id, out var gone))
                {
                    db.ConnectedCalendarEvents.Remove(gone);
                    removed++;
                }
            }

            if (result.DeltaCursor is not null)
            {
                delta = result.DeltaCursor;
                break;
            }

            if (result.NextPage is null) break;
            cursor = result.NextPage;
        }

        if (delta is null) throw new ProviderAuthException("read_incomplete", revoked: false);

        // A full read is the whole window: whatever was stored and not seen again is gone.
        if (full)
        {
            foreach (var stale in existing.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList())
            {
                db.ConnectedCalendarEvents.Remove(stale);
                existing.Remove(stale.ExternalEventId);
                removed++;
            }
        }

        calendar.RecordSync(delta, windowStart, existing.Count, now);
        return (upserted, removed);
    }

    /// <summary>Forgets what an agenda brought — unticked, or its account removed.</summary>
    public async Task ForgetEventsAsync(IReadOnlyCollection<Guid> calendarIds, CancellationToken ct)
    {
        if (calendarIds.Count == 0) return;
        db.ConnectedCalendarEvents.RemoveRange(
            await db.ConnectedCalendarEvents.AsTracking().Where(e => calendarIds.Contains(e.CalendarId)).ToListAsync(ct));
    }
}

/// <summary>
/// Every 15 minutes, GLOBAL: one <see cref="SyncConnectedCalendarsJob"/> per active tenant — the
/// shape of <c>IntegrationSyncOrchestrator</c>, so a tenant's failure costs that tenant its run.
/// The providers' push notifications (Graph subscriptions, Google watch channels) are a later
/// improvement; this is the floor that keeps every agenda at most 15 minutes behind without them.
/// </summary>
public sealed class SyncConnectedCalendarsOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<SyncConnectedCalendarsOrchestrator> logger)
{
    public const string CronExpression = "*/15 * * * *";
    public const string RecurringJobId = "integration-connected-calendars-sync";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);
        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;
            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<SyncConnectedCalendarsJob>(job => job.ExecuteAsync(tenantId));
            enqueued++;
        }

        logger.LogDebug("Connected agendas: {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}

/// <summary>One tenant's connected accounts, one after the other, each in isolation.</summary>
public sealed class SyncConnectedCalendarsJob(IServiceScopeFactory scopeFactory, ILogger<SyncConnectedCalendarsJob> logger)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        var ids = await db.ConnectedAccounts
            .Where(a => a.Status == ConnectedAccountStatus.Active && a.Provider != ConnectedAccountProvider.Imap)
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var id in ids)
        {
            // A scope per account: one account's tracked entities and failures never leak into the next.
            using var accountScope = scopeFactory.CreateScope();
            var accountDb = accountScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

            try
            {
                var account = await accountDb.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == id);
                if (account is null) continue;

                await accountScope.ServiceProvider.GetRequiredService<ConnectedCalendarSync>().SyncAccountAsync(account, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Connected account {AccountId} of tenant {TenantId}: agenda sync failed.", id, tenantId);
            }
        }

        // Then the rendez-vous copies a provider refused earlier (lot 4).
        using var copyScope = scopeFactory.CreateScope();
        await copyScope.ServiceProvider.GetRequiredService<AppointmentCalendarCopies>().RetryFailedAsync(tenantId, CancellationToken.None);
    }
}

/// <summary>Reads one account now — after the user ticks an agenda, out of the request's way.</summary>
public sealed class SyncConnectedAccountJob(IServiceScopeFactory scopeFactory, ILogger<SyncConnectedAccountJob> logger)
{
    public async Task ExecuteAsync(Guid tenantId, Guid accountId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null) return;

        var result = await scope.ServiceProvider.GetRequiredService<ConnectedCalendarSync>().SyncAccountAsync(account, CancellationToken.None);
        if (result.IsFailure) logger.LogInformation("Connected account {AccountId}: sync not done ({Code}).", accountId, result.Error);
    }
}
