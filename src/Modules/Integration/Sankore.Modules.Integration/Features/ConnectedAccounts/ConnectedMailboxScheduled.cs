namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// Connected mailbox: « Programmer l'envoi » and the mailbox signature (owner's request,
// 2026-10-29, with the composer's formatting bar).

// ── Scheduled send ───────────────────────────────────────────────────────────────────────────

/// <summary>What a scheduled message carries, encrypted in <see cref="ScheduledMail.PayloadEnc"/>.</summary>
internal sealed record ScheduledMailPayload(
    List<string> To,
    List<string> Cc,
    List<string> Bcc,
    string Subject,
    string Body,
    Guid? ReplyToMessageId,
    Guid? ForwardMessageId,
    List<string> ForwardAttachmentIds);

public sealed record ScheduledMailDto(
    Guid Id,
    Guid AccountId,
    string AccountLabel,
    DateTimeOffset SendAt,
    string Status,
    string? LastError,
    string? Subject,
    IReadOnlyList<string> To,
    int AttachmentCount,
    bool IsReply);

internal sealed record ListMyScheduledMailsQuery : IRequest<IReadOnlyList<ScheduledMailDto>>;

/// <summary>The caller's messages waiting to leave, soonest first — decrypted for their owner only.</summary>
internal sealed class ListMyScheduledMailsHandler(IntegrationDbContext db, ICurrentUser me, CalendarEventProtector protector)
    : IRequestHandler<ListMyScheduledMailsQuery, IReadOnlyList<ScheduledMailDto>>
{
    public async Task<IReadOnlyList<ScheduledMailDto>> Handle(ListMyScheduledMailsQuery query, CancellationToken ct)
    {
        var mails = await db.ScheduledMails.Where(m => m.UserId == me.Id).OrderBy(m => m.SendAt).ToListAsync(ct);
        if (mails.Count == 0) return [];

        var ids = mails.Select(m => m.Id).ToList();
        var files = await db.ScheduledMailAttachments.Where(a => ids.Contains(a.ScheduledMailId))
            .GroupBy(a => a.ScheduledMailId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var accountIds = mails.Select(m => m.AccountId).Distinct().ToList();
        var labels = await db.ConnectedAccounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Label, ct);

        return mails.Select(m =>
        {
            var payload = ScheduledMails.Reveal(protector, m);
            return new ScheduledMailDto(
                m.Id, m.AccountId, labels.GetValueOrDefault(m.AccountId) ?? string.Empty, m.SendAt, m.Status.ToString(), m.LastError,
                payload?.Subject, payload?.To ?? [], files.GetValueOrDefault(m.Id), payload?.ReplyToMessageId is not null);
        }).ToList();
    }
}

internal sealed record CancelScheduledMailCommand(Guid Id) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ScheduledMail";
    public string? ResourceId => Id.ToString();
}

/// <summary>Deletes a message that has not left — files included. One being sent can no longer be held back.</summary>
internal sealed class CancelScheduledMailHandler(IntegrationDbContext db, ICurrentUser me) : IRequestHandler<CancelScheduledMailCommand, Result>
{
    public async Task<Result> Handle(CancelScheduledMailCommand cmd, CancellationToken ct)
    {
        var mail = await db.ScheduledMails.AsTracking().FirstOrDefaultAsync(m => m.Id == cmd.Id && m.UserId == me.Id, ct);
        if (mail is null) return Result.Fail(ScheduledMails.NotFound);
        if (mail.Status == ScheduledMailStatus.Sending) return Result.Fail(ScheduledMails.AlreadySending);

        await ScheduledMails.RemoveAsync(db, [mail], ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record RetryScheduledMailCommand(Guid Id) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ScheduledMail";
    public string? ResourceId => Id.ToString();
}

/// <summary>A message that did not leave at its time goes now — once the user has fixed what stopped it.</summary>
internal sealed class RetryScheduledMailHandler(IntegrationDbContext db, ICurrentUser me, TimeProvider clock, IBackgroundJobClient jobs)
    : IRequestHandler<RetryScheduledMailCommand, Result>
{
    public async Task<Result> Handle(RetryScheduledMailCommand cmd, CancellationToken ct)
    {
        var mail = await db.ScheduledMails.AsTracking().FirstOrDefaultAsync(m => m.Id == cmd.Id && m.UserId == me.Id, ct);
        if (mail is null) return Result.Fail(ScheduledMails.NotFound);

        var retried = mail.Retry(clock.GetUtcNow());
        if (retried.IsFailure) return retried;
        await db.SaveChangesAsync(ct);

        var (tenantId, id) = (me.TenantId, mail.Id);
        jobs.Enqueue<SendScheduledMailJob>(job => job.ExecuteAsync(tenantId, id));
        return Result.Ok();
    }
}

internal static class ScheduledMails
{
    public const string NotFound = "MAIL_SCHEDULE_NOT_FOUND";
    public const string AlreadySending = "MAIL_SCHEDULE_SENDING";

    public static ScheduledMailPayload? Reveal(CalendarEventProtector protector, ScheduledMail mail)
    {
        var json = protector.Reveal(mail.PayloadEnc);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<ScheduledMailPayload>(json);
    }

    /// <summary>The messages and their files. The caller saves.</summary>
    public static async Task RemoveAsync(IntegrationDbContext db, IReadOnlyCollection<ScheduledMail> mails, CancellationToken ct)
    {
        var ids = mails.Select(m => m.Id).ToList();
        db.ScheduledMailAttachments.RemoveRange(await db.ScheduledMailAttachments.AsTracking().Where(a => ids.Contains(a.ScheduledMailId)).ToListAsync(ct));
        db.ScheduledMails.RemoveRange(mails);
    }

    /// <summary>A mailbox turned off or removed takes its waiting messages with it: nothing would send them.</summary>
    public static async Task ForgetAccountAsync(IntegrationDbContext db, Guid accountId, CancellationToken ct) =>
        await RemoveAsync(db, await db.ScheduledMails.AsTracking().Where(m => m.AccountId == accountId).ToListAsync(ct), ct);
}

/// <summary>
/// Sends one scheduled message at its time, AS ITS OWNER: the send goes through the same command
/// as an immediate one — mailbox checks, threading, the sync that follows, the audit row — under
/// the owner's identity, which is what makes « sent from my mailbox » true at 8 a.m. too.
///
/// <para>
/// Scheduled by Hangfire at the instant itself (<c>BackgroundJob.Schedule</c>), which survives a
/// restart. Taken once: <see cref="ScheduledMail.TryStartSending"/> refuses a second run, and a
/// message cancelled meanwhile is simply gone. On failure the message stays « Échec », the owner
/// is told by the bell, and nothing is retried on its own — a refused password or a revoked token
/// needs them first.
/// </para>
/// </summary>
public sealed class SendScheduledMailJob(IServiceScopeFactory scopeFactory, ILogger<SendScheduledMailJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid tenantId, Guid scheduledId)
    {
        Guid ownerId;

        using (BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM"))
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var mail = await db.ScheduledMails.AsTracking().FirstOrDefaultAsync(m => m.Id == scheduledId);
            if (mail is null || !mail.TryStartSending()) return;
            await db.SaveChangesAsync();
            ownerId = mail.UserId;
        }

        using (BackgroundJobContext.SetScope(tenantId, ownerId, "Envoi programmé"))
        using (var scope = scopeFactory.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var protector = sp.GetRequiredService<CalendarEventProtector>();
            var clock = sp.GetRequiredService<TimeProvider>();
            var mail = await db.ScheduledMails.AsTracking().FirstAsync(m => m.Id == scheduledId);
            var payload = ScheduledMails.Reveal(protector, mail);

            string? error;
            try
            {
                error = payload is null ? MailSendException.Failed : await SendAsync(sp, db, protector, mail, payload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled mail {ScheduledId}: sending failed.", scheduledId);
                error = MailSendException.Failed;
            }

            if (error is null)
            {
                await ScheduledMails.RemoveAsync(db, [mail], CancellationToken.None);
                await db.SaveChangesAsync();
                return;
            }

            mail.Fail(error, clock.GetUtcNow());
            await db.SaveChangesAsync();
            logger.LogWarning("Scheduled mail {ScheduledId} did not leave ({Code}).", scheduledId, error);
            await NotifyAsync(sp, tenantId, mail, payload, error);
        }
    }

    private static async Task<string?> SendAsync(
        IServiceProvider sp, IntegrationDbContext db, CalendarEventProtector protector, ScheduledMail mail, ScheduledMailPayload payload)
    {
        var files = await db.ScheduledMailAttachments.Where(a => a.ScheduledMailId == mail.Id).ToListAsync();
        var attachments = files
            .Select(f => new OutgoingAttachment(f.Name, f.ContentType, Convert.FromBase64String(protector.Reveal(f.ContentEnc) ?? string.Empty)))
            .ToList();

        var result = await sp.GetRequiredService<ISender>().Send(new SendMailCommand(
            mail.AccountId, payload.To, payload.Cc, payload.Bcc, payload.Subject, payload.Body,
            payload.ReplyToMessageId, payload.ForwardMessageId, payload.ForwardAttachmentIds, attachments));

        return result.IsSuccess ? null : result.Error ?? MailSendException.Failed;
    }

    private async Task NotifyAsync(IServiceProvider sp, Guid tenantId, ScheduledMail mail, ScheduledMailPayload? payload, string error)
    {
        try
        {
            var subject = string.IsNullOrWhiteSpace(payload?.Subject) ? "(sans objet)" : payload!.Subject;
            await sp.GetRequiredService<INotificationsModule>().NotifyInAppAsync(new InAppNotificationRequest(
                tenantId, mail.UserId, "integration.mail.scheduled-failed", "Un e-mail programmé n’est pas parti",
                $"« {subject} » — ouvrez la Messagerie pour le renvoyer ou le supprimer.", "/tasks/mail?programmes=1",
                $"mail.scheduled.failed:{mail.Id}:{mail.FailedAt?.ToUnixTimeSeconds()}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled mail {ScheduledId}: the owner could not be told it failed ({Code}).", mail.Id, error);
        }
    }
}

// ── Signature ────────────────────────────────────────────────────────────────────────────────

public sealed record SetMailSignatureRequest(string? Signature);

internal sealed record SetMailSignatureCommand(Guid AccountId, string? Signature) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

internal sealed class SetMailSignatureHandler(IntegrationDbContext db, ICurrentUser me) : IRequestHandler<SetMailSignatureCommand, Result>
{
    public async Task<Result> Handle(SetMailSignatureCommand cmd, CancellationToken ct)
    {
        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Result.Fail(ConnectedAccountErrors.NotFound);

        var set = account.SetMailSignature(cmd.Signature);
        if (set.IsFailure) return set;
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

internal static class ConnectedMailboxScheduledEndpoints
{
    internal static IEndpointRouteBuilder MapConnectedMailboxScheduledEndpoints(this IEndpointRouteBuilder app)
    {
        var mail = app.MapGroup("integration/me/mail/scheduled")
            .WithTags("ConnectedMailbox")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code);

        mail.MapGet("", async (ISender sender, CancellationToken ct) => Results.Ok(await sender.Send(new ListMyScheduledMailsQuery(), ct)))
            .WithName("ListMyScheduledMails")
            .WithSummary("The caller's messages waiting to leave (or that failed to), soonest first")
            .Produces<IReadOnlyList<ScheduledMailDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        mail.MapDelete("{id:guid}", async (Guid id, ISender sender, CancellationToken ct) => Map(await sender.Send(new CancelScheduledMailCommand(id), ct)))
            .WithName("CancelScheduledMail")
            .WithSummary("Cancels a scheduled message: it is deleted with its files")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        mail.MapPost("{id:guid}/retry", async (Guid id, ISender sender, CancellationToken ct) => Map(await sender.Send(new RetryScheduledMailCommand(id), ct)))
            .WithName("RetryScheduledMail")
            .WithSummary("Sends now a scheduled message that failed to leave")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPut("integration/connected-accounts/{id:guid}/mail/signature",
                async (Guid id, SetMailSignatureRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SetMailSignatureCommand(id, req.Signature), ct)))
            .WithTags("ConnectedMailbox")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code)
            .WithName("SetMailSignature")
            .WithSummary("Sets (or clears, with null) the signature the composer adds under messages from this mailbox")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static IResult Map(Result result) => result.IsSuccess
        ? Results.NoContent()
        : result.Error switch
        {
            ScheduledMails.NotFound or ConnectedAccountErrors.NotFound
                => Results.Problem(title: "Messagerie", detail: result.Error, statusCode: StatusCodes.Status404NotFound),
            ScheduledMails.AlreadySending
                => Results.Problem(title: "Messagerie", detail: result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(title: "Messagerie", detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity),
        };
}
