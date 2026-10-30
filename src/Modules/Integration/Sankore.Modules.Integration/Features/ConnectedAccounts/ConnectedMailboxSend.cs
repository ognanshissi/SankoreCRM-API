namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Text.RegularExpressions;
using System.Transactions;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using MailAddress = System.Net.Mail.MailAddress;

// Connected mailbox, lot 3: sending from the user's own mailbox (new, reply, forward), marking
// read at the provider, opening an attachment, and the institution's e-mail templates.

// ── Limits ───────────────────────────────────────────────────────────────────────────────────

internal static class MailSendLimits
{
    public const int MaxRecipients = 50;
    public const int MaxSubject = 998;
    public const int MaxBody = 1_000_000;
    public const int MaxAttachments = 10;

    /// <summary>Under every provider's own ceiling (Gmail 25 MB once encoded, Outlook's default 25 MB).</summary>
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;

    /// <summary>What every mail provider refuses or quarantines anyway: refused here with a reason instead.</summary>
    public static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".ps1", ".jar", ".lnk",
    };
}

internal static class MailSendErrors
{
    public const string RecipientsRequired = "MAIL_RECIPIENTS_REQUIRED";
    public const string RecipientInvalid = "MAIL_RECIPIENT_INVALID";
    public const string TooManyRecipients = "MAIL_TOO_MANY_RECIPIENTS";
    public const string SubjectInvalid = "MAIL_SUBJECT_INVALID";
    public const string BodyTooLarge = "MAIL_BODY_TOO_LARGE";
    public const string TooManyAttachments = "MAIL_TOO_MANY_ATTACHMENTS";
    public const string AttachmentsTooLarge = "MAIL_ATTACHMENTS_TOO_LARGE";
    public const string AttachmentBlocked = "MAIL_ATTACHMENT_BLOCKED";
    public const string MailboxDisabled = "CONNECTED_MAILBOX_DISABLED";
    public const string MessageNotFound = "MAIL_MESSAGE_NOT_FOUND";
    public const string AttachmentNotFound = "MAIL_ATTACHMENT_NOT_FOUND";
    public const string OtherMailbox = "MAIL_MESSAGE_OF_ANOTHER_MAILBOX";
    /// <summary>The client named as recipient is unknown, outside the caller's perimeter, restricted, or has no address.</summary>
    public const string ClientNotReachable = "MAIL_CLIENT_NOT_REACHABLE";
    public const string ClientRestricted = "MAIL_CLIENT_RESTRICTED";
    public const string ClientEmailMissing = "MAIL_CLIENT_EMAIL_MISSING";
    /// <summary>A scheduled time in less than a minute, or more than a year away.</summary>
    public const string ScheduleInvalid = "MAIL_SCHEDULE_INVALID";
    /// <summary>The tenant is in test mode and a recipient is not one of its users.</summary>
    public const string TestRecipient = "MAIL_TEST_RECIPIENT";
}

// ── Send ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Everything a person writes or is named by is <see cref="SensitiveDataAttribute"/>: the audit
/// row says that the caller sent from that mailbox, in answer to which message — not to whom or what.
/// </summary>
internal sealed record SendMailCommand(
    Guid AccountId,
    [property: SensitiveData] IReadOnlyList<string> To,
    [property: SensitiveData] IReadOnlyList<string> Cc,
    [property: SensitiveData] IReadOnlyList<string> Bcc,
    [property: SensitiveData] string Subject,
    [property: SensitiveData] string Body,
    Guid? ReplyToMessageId,
    Guid? ForwardMessageId,
    IReadOnlyList<string> ForwardAttachmentIds,
    [property: SensitiveData] IReadOnlyList<OutgoingAttachment> Attachments,
    Guid? ToClientId = null,
    DateTimeOffset? SendAt = null)
    : IRequest<Result<SendMailOutcome>>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedAccount";
    public string? ResourceId => AccountId.ToString();
}

/// <summary>What a send did: left now (no id), or kept to leave at <paramref name="SendAt"/>.</summary>
public sealed record SendMailOutcome(Guid? ScheduledId, DateTimeOffset? SendAt);

/// <summary>
/// Sends from one of the caller's mailboxes. A REPLY goes from the mailbox the answered message
/// was read from and is threaded at the provider; a FORWARD may carry the original's attachments,
/// fetched from the provider at send time. Nothing is written here: the message lands in the
/// mailbox's « Envoyés », and the sync started right after keeps it if a lead or a client is among
/// its recipients — the same rule as every other message.
///
/// <para>
/// <b>Outside the command's transaction.</b> Sending is a network call of seconds, and the ambient
/// TransactionScope times out after a minute: an abort AFTER the provider accepted the message
/// would answer an error for an e-mail that left, and the agent would send it twice. The work runs
/// under a suppressed scope; the only write — an account the provider stopped trusting — commits
/// on its own.
/// </para>
/// </summary>
internal sealed class SendMailHandler(
    IntegrationDbContext db,
    ICurrentUser me,
    IEnumerable<IMailboxReader> readers,
    ConnectedMailboxSync sync,
    ILogger<SendMailHandler> logger,
    IBackgroundJobClient? jobs = null,
    ICustomersModule? customers = null,
    CalendarEventProtector? protector = null,
    TimeProvider? clock = null,
    IAdministrationModule? administration = null)
    : IRequestHandler<SendMailCommand, Result<SendMailOutcome>>
{
    public async Task<Result<SendMailOutcome>> Handle(SendMailCommand cmd, CancellationToken ct)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var result = await SendAsync(cmd, ct);
        suppressed.Complete();
        return result;
    }

    private async Task<Result<SendMailOutcome>> SendAsync(SendMailCommand cmd, CancellationToken ct)
    {
        var to = Parse(cmd.To);
        var cc = Parse(cmd.Cc);
        var bcc = Parse(cmd.Bcc);
        if (to is null || cc is null || bcc is null) return Fail(MailSendErrors.RecipientInvalid);

        if (cmd.ToClientId is { } clientId)
        {
            var client = await ClientAddressAsync(clientId, ct);
            if (client.IsFailure) return Fail(client.Error!);
            if (!to.Any(p => string.Equals(p.Address, client.Value, StringComparison.OrdinalIgnoreCase)))
                to.Insert(0, new MailParty(client.Value!, null));
        }
        if (to.Count + cc.Count + bcc.Count == 0) return Fail(MailSendErrors.RecipientsRequired);
        if (to.Count + cc.Count + bcc.Count > MailSendLimits.MaxRecipients) return Fail(MailSendErrors.TooManyRecipients);

        // A test tenant writes only to its team — every recipient, the client resolved above
        // included. A scheduled message goes through here again when it leaves.
        if (administration is not null)
        {
            foreach (var party in to.Concat(cc).Concat(bcc))
                if (await administration.IsEmailBlockedInTestAsync(me.TenantId, party.Address, ct))
                    return Fail(MailSendErrors.TestRecipient);
        }

        var subject = cmd.Subject?.Trim() ?? string.Empty;
        if (subject.Length > MailSendLimits.MaxSubject || subject.Contains('\n') || subject.Contains('\r')) return Fail(MailSendErrors.SubjectInvalid);
        if ((cmd.Body?.Length ?? 0) > MailSendLimits.MaxBody) return Fail(MailSendErrors.BodyTooLarge);

        var attachmentCheck = CheckAttachments(cmd.Attachments.Select(a => (a.Name, (long)a.Content.Length)), cmd.ForwardAttachmentIds.Count);
        if (attachmentCheck.IsFailure) return Fail(attachmentCheck.Error!);

        var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AccountId && a.UserId == me.Id, ct);
        if (account is null) return Fail(ConnectedAccountErrors.NotFound);
        if (!account.IsMailEnabled) return Fail(MailSendErrors.MailboxDisabled);
        if (account.Status == ConnectedAccountStatus.NeedsReconnect) return Fail(ConnectedAccountErrors.NeedsReconnect);

        OutgoingReplyTo? replyTo = null;
        if (cmd.ReplyToMessageId is { } replyId)
        {
            var answered = await OwnMessageAsync(replyId, ct);
            if (answered is null) return Fail(MailSendErrors.MessageNotFound);
            if (answered.AccountId != account.Id) return Fail(MailSendErrors.OtherMailbox);
            replyTo = new OutgoingReplyTo(answered.ExternalId, answered.InternetMessageId, answered.ThreadKey);
        }

        ConnectedMailMessage? forwarded = null;
        List<MailAttachmentInfo> forwardedAttachments = [];
        if (cmd.ForwardMessageId is { } forwardId)
        {
            forwarded = await OwnMessageAsync(forwardId, ct);
            if (forwarded is null) return Fail(MailSendErrors.MessageNotFound);
            if (forwarded.AccountId != account.Id && cmd.ForwardAttachmentIds.Count > 0) return Fail(MailSendErrors.OtherMailbox);

            var known = sync.Reveal<List<MailAttachmentInfo>>(forwarded.AttachmentsEnc) ?? [];
            foreach (var id in cmd.ForwardAttachmentIds.Distinct())
            {
                if (known.FirstOrDefault(a => a.Id == id) is not { } info) return Fail(MailSendErrors.AttachmentNotFound);
                forwardedAttachments.Add(info);
            }

            var all = CheckAttachments(
                cmd.Attachments.Select(a => (a.Name, (long)a.Content.Length)).Concat(forwardedAttachments.Select(a => (a.Name, a.Size))), 0);
            if (all.IsFailure) return Fail(all.Error!);
        }

        // Programmé : tout ce qui précède a été vérifié comme pour un envoi immédiat — le client a été
        // adressé ICI, avec les droits de l'agent, et c'est son adresse qui est gardée.
        if (cmd.SendAt is { } sendAt) return await ScheduleAsync(cmd, account, to, cc, bcc, subject, sendAt, ct);

        var reader = readers.FirstOrDefault(r => r.CanRead(account));
        if (reader is null) return Fail(ConnectedAccountErrors.ProviderUnavailable);

        try
        {
            var opened = await reader.OpenAsync(account, ct);
            if (opened.IsFailure) return Fail((await RefusedAsync(account, opened.Error!, ct)).Error!);
            await using var session = opened.Value!;

            var attachments = cmd.Attachments.ToList();
            foreach (var info in forwardedAttachments)
            {
                var bytes = await reader.ReadAttachmentAsync(session, forwarded!.ExternalId, info.Id, ct);
                if (bytes is null) return Fail(MailSendErrors.AttachmentNotFound);
                attachments.Add(new OutgoingAttachment(info.Name, info.ContentType ?? "application/octet-stream", bytes));
            }

            if (attachments.Sum(a => (long)a.Content.Length) > MailSendLimits.MaxAttachmentBytes)
                return Fail(MailSendErrors.AttachmentsTooLarge);

            await reader.SendAsync(session, account, new OutgoingMail(
                new MailParty(account.Email, account.DisplayName), to, cc, bcc, subject, cmd.Body ?? string.Empty, attachments, replyTo), ct);
        }
        catch (MailSendException ex)
        {
            logger.LogWarning("Mailbox {AccountId}: sending refused ({Code}).", account.Id, ex.Code);
            return Fail((await RefusedAsync(account, ex.Code, ct)).Error!);
        }
        catch (ProviderAuthException ex)
        {
            logger.LogWarning("Mailbox {AccountId}: sending failed ({Code}).", account.Id, ex.Code);
            if (!ex.Revoked) return Fail(MailSendException.Failed);
            account.MarkNeedsReconnect(ex.Code);
            await db.SaveChangesAsync(ct);
            return Fail(ConnectedAccountErrors.NeedsReconnect);
        }

        // The sent copy is read back like any other message — and kept if it went to a lead or a client.
        var (tenantId, accountId) = (account.TenantId, account.Id);
        jobs?.Enqueue<SyncConnectedMailboxJob>(job => job.ExecuteAsync(tenantId, accountId));
        return Result.Ok(new SendMailOutcome(null, null));
    }

    private static Result<SendMailOutcome> Fail(string code) => Result.Fail<SendMailOutcome>(code);

    /// <summary>
    /// Keeps the message, encrypted, for <see cref="SendScheduledMailJob"/> — the files too, since
    /// the browser that sent them will be long closed. Reply and forward keep their references: the
    /// answered message, and the forwarded files, are read again when it leaves.
    /// </summary>
    private async Task<Result<SendMailOutcome>> ScheduleAsync(
        SendMailCommand cmd, ConnectedAccount account, List<MailParty> to, List<MailParty> cc, List<MailParty> bcc,
        string subject, DateTimeOffset sendAt, CancellationToken ct)
    {
        if (protector is null) return Fail(MailSendException.Failed);
        var now = (clock ?? TimeProvider.System).GetUtcNow();

        var payload = new ScheduledMailPayload(
            to.Select(p => p.Address).ToList(), cc.Select(p => p.Address).ToList(), bcc.Select(p => p.Address).ToList(),
            subject, cmd.Body ?? string.Empty, cmd.ReplyToMessageId, cmd.ForwardMessageId, cmd.ForwardAttachmentIds.ToList());

        var created = ScheduledMail.Create(me.TenantId, me.Id, account.Id, sendAt.ToUniversalTime(),
            protector.Protect(System.Text.Json.JsonSerializer.Serialize(payload))!, now);
        if (created.IsFailure) return Fail(MailSendErrors.ScheduleInvalid);

        var mail = created.Value!;
        db.ScheduledMails.Add(mail);
        foreach (var file in cmd.Attachments)
        {
            db.ScheduledMailAttachments.Add(ScheduledMailAttachment.Create(
                me.TenantId, mail.Id, file.Name, file.ContentType, file.Content.Length, protector.Protect(Convert.ToBase64String(file.Content))!));
        }

        await db.SaveChangesAsync(ct);

        var (tenantId, id) = (me.TenantId, mail.Id);
        jobs?.Schedule<SendScheduledMailJob>(job => job.ExecuteAsync(tenantId, id), mail.SendAt);
        return Result.Ok(new SendMailOutcome(mail.Id, mail.SendAt));
    }

    /// <summary>
    /// The client's address, read by M01 for this message only — the browser named the RECORD and
    /// never saw the address, which its screen shows masked. Same posture as click-to-call: the
    /// owning module applies the caller's perimeter and the purge restriction. Its refusals are
    /// translated to this module's codes; an outage is a failed send the agent can retry.
    /// </summary>
    private async Task<Result<string>> ClientAddressAsync(Guid clientId, CancellationToken ct)
    {
        if (customers is null) return Result.Fail<string>(MailSendErrors.ClientNotReachable);

        try
        {
            var resolved = await customers.ResolveEmailForMailAsync(me.TenantId, clientId, me.Id, ct);
            if (resolved.IsSuccess) return resolved;

            return Result.Fail<string>(resolved.Error switch
            {
                "CLIENT_PROCESSING_RESTRICTED" => MailSendErrors.ClientRestricted,
                "CLIENT_EMAIL_MISSING" => MailSendErrors.ClientEmailMissing,
                _ => MailSendErrors.ClientNotReachable,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve the address of client {ClientId} for a message.", clientId);
            return Result.Fail<string>(MailSendException.Failed);
        }
    }

    /// <summary>A refused IMAP/SMTP password is the user's to fix, like a revoked token.</summary>
    private async Task<Result> RefusedAsync(ConnectedAccount account, string code, CancellationToken ct)
    {
        if (code is "MAIL_IMAP_AUTH_FAILED" or "MAIL_SMTP_AUTH_FAILED")
        {
            account.MarkNeedsReconnect(code);
            await db.SaveChangesAsync(ct);
        }

        return Result.Fail(code);
    }

    private Task<ConnectedMailMessage?> OwnMessageAsync(Guid id, CancellationToken ct) =>
        db.ConnectedMailMessages.FirstOrDefaultAsync(m => m.Id == id && m.UserId == me.Id, ct);

    /// <summary>Bare addresses only — « Nom &lt;a@b&gt; » is refused, the name is not the agent's to invent. Null on any invalid one.</summary>
    internal static List<MailParty>? Parse(IEnumerable<string>? values)
    {
        var parties = new List<MailParty>();

        foreach (var raw in values ?? [])
        {
            foreach (var value in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var email = ImapMailboxInput.Email(value);
                if (email is null) return null;
                if (!parties.Any(p => string.Equals(p.Address, email, StringComparison.OrdinalIgnoreCase))) parties.Add(new MailParty(email, null));
            }
        }

        return parties;
    }

    internal static Result CheckAttachments(IEnumerable<(string Name, long Size)> files, int extra)
    {
        var list = files.ToList();
        if (list.Count + extra > MailSendLimits.MaxAttachments) return Result.Fail(MailSendErrors.TooManyAttachments);
        if (list.Sum(f => f.Size) > MailSendLimits.MaxAttachmentBytes) return Result.Fail(MailSendErrors.AttachmentsTooLarge);
        if (list.Any(f => MailSendLimits.BlockedExtensions.Contains(Path.GetExtension(f.Name)))) return Result.Fail(MailSendErrors.AttachmentBlocked);
        return Result.Ok();
    }
}

// ── Read / unread ────────────────────────────────────────────────────────────────────────────

internal sealed record SetMailThreadReadCommand(Guid MessageId, bool IsRead) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ConnectedMailMessage";
    public string? ResourceId => MessageId.ToString();
}

/// <summary>
/// Marks a whole conversation read (or unread) — at the PROVIDER first, so Outlook or Gmail says
/// the same thing, then here. A message the provider refused stays as it was; the next sync would
/// have put it back anyway. Network outside the transaction, like the sending.
/// </summary>
internal sealed class SetMailThreadReadHandler(
    IntegrationDbContext db, ICurrentUser me, IEnumerable<IMailboxReader> readers, TimeProvider clock, ILogger<SetMailThreadReadHandler> logger)
    : IRequestHandler<SetMailThreadReadCommand, Result>
{
    public async Task<Result> Handle(SetMailThreadReadCommand cmd, CancellationToken ct)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var anchor = await db.ConnectedMailMessages.FirstOrDefaultAsync(m => m.Id == cmd.MessageId && m.UserId == me.Id, ct);
        if (anchor is null) return Result.Fail(MailSendErrors.MessageNotFound);

        var messages = await db.ConnectedMailMessages.AsTracking()
            .Where(m => m.AccountId == anchor.AccountId && m.ThreadKey == anchor.ThreadKey && m.UserId == me.Id && m.IsRead != cmd.IsRead)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            suppressed.Complete();
            return Result.Ok();
        }

        var account = await db.ConnectedAccounts.AsTracking().FirstAsync(a => a.Id == anchor.AccountId, ct);
        var reader = readers.FirstOrDefault(r => r.CanRead(account));
        if (reader is null || !account.IsMailEnabled) return Result.Fail(MailSendErrors.MailboxDisabled);

        var opened = await reader.OpenAsync(account, ct);
        if (opened.IsFailure) return Result.Fail(opened.Error!);

        await using (var session = opened.Value!)
        {
            foreach (var message in messages)
            {
                try
                {
                    await reader.SetReadAsync(session, message.ExternalId, cmd.IsRead, ct);
                    message.Refresh(cmd.IsRead, message.Folder, clock.GetUtcNow());
                }
                catch (Exception ex) when (ex is ProviderAuthException or MailSendException or IOException or MailKit.ProtocolException or MailKit.CommandException)
                {
                    logger.LogInformation("Mailbox {AccountId}: read flag not changed at the provider ({Reason}).", account.Id, ex.GetType().Name);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        suppressed.Complete();
        return Result.Ok();
    }
}

// ── Attachment ───────────────────────────────────────────────────────────────────────────────

internal sealed record MailAttachmentFile(string Name, string ContentType, byte[] Content);

internal sealed record GetMailAttachmentQuery(Guid MessageId, string AttachmentId) : IRequest<Result<MailAttachmentFile>>;

/// <summary>An attachment of one of the caller's kept messages, fetched from the provider now — its bytes are never stored here.</summary>
internal sealed class GetMailAttachmentHandler(
    IntegrationDbContext db, ICurrentUser me, IEnumerable<IMailboxReader> readers, ConnectedMailboxSync sync)
    : IRequestHandler<GetMailAttachmentQuery, Result<MailAttachmentFile>>
{
    public async Task<Result<MailAttachmentFile>> Handle(GetMailAttachmentQuery query, CancellationToken ct)
    {
        var message = await db.ConnectedMailMessages.FirstOrDefaultAsync(m => m.Id == query.MessageId && m.UserId == me.Id, ct);
        if (message is null) return Result.Fail<MailAttachmentFile>(MailSendErrors.MessageNotFound);

        var info = (sync.Reveal<List<MailAttachmentInfo>>(message.AttachmentsEnc) ?? []).FirstOrDefault(a => a.Id == query.AttachmentId);
        if (info is null) return Result.Fail<MailAttachmentFile>(MailSendErrors.AttachmentNotFound);

        var account = await db.ConnectedAccounts.FirstAsync(a => a.Id == message.AccountId, ct);
        var reader = readers.FirstOrDefault(r => r.CanRead(account));
        if (reader is null || !account.IsMailEnabled) return Result.Fail<MailAttachmentFile>(MailSendErrors.MailboxDisabled);

        var opened = await reader.OpenAsync(account, ct);
        if (opened.IsFailure) return Result.Fail<MailAttachmentFile>(opened.Error!);

        try
        {
            await using var session = opened.Value!;
            var bytes = await reader.ReadAttachmentAsync(session, message.ExternalId, info.Id, ct);
            return bytes is null
                ? Result.Fail<MailAttachmentFile>(MailSendErrors.AttachmentNotFound)
                : Result.Ok(new MailAttachmentFile(info.Name, info.ContentType ?? "application/octet-stream", bytes));
        }
        catch (ProviderAuthException ex)
        {
            return Result.Fail<MailAttachmentFile>(ex.Revoked ? ConnectedAccountErrors.NeedsReconnect : ConnectedAccountErrors.ProviderError);
        }
    }
}

// ── Templates ────────────────────────────────────────────────────────────────────────────────

public sealed record MailTemplateDto(Guid Id, string Name, string Subject, string Body, bool IsArchived, DateTimeOffset UpdatedAt);

public sealed record SaveMailTemplateRequest(string Name, string Subject, string Body);

public sealed record ArchiveMailTemplateRequest(bool Archived);

/// <param name="Key">What to write between double braces: <c>{{destinataire.nom}}</c>.</param>
public sealed record MailTemplateVariableDto(string Key, string Label);

/// <param name="LeadId">The lead written to, for <c>destinataire.*</c>.</param>
/// <param name="ClientId">Or the client written to — <c>client.numero</c> too.</param>
/// <param name="AccountId">The mailbox sending, for <c>agent.email</c>.</param>
public sealed record RenderMailTemplateRequest(Guid? LeadId, Guid? ClientId, Guid? AccountId);

/// <param name="Missing">Variables the template uses that had no value here — left blank, shown to the agent before sending.</param>
public sealed record RenderedMailTemplateDto(string Subject, string Body, IReadOnlyList<string> Missing);

internal sealed record ListMailTemplatesQuery(bool IncludeArchived) : IRequest<IReadOnlyList<MailTemplateDto>>;

internal sealed class ListMailTemplatesHandler(IntegrationDbContext db, ICurrentUser me) : IRequestHandler<ListMailTemplatesQuery, IReadOnlyList<MailTemplateDto>>
{
    public async Task<IReadOnlyList<MailTemplateDto>> Handle(ListMailTemplatesQuery query, CancellationToken ct)
    {
        // Archived templates are the editor's business, not the agent's.
        var withArchived = query.IncludeArchived && me.Permissions.Contains(Permissions.CanManageMailTemplates.Code);

        return await db.MailTemplates
            .Where(t => withArchived || !t.IsArchived)
            .OrderBy(t => t.IsArchived).ThenBy(t => t.Name)
            .Select(t => new MailTemplateDto(t.Id, t.Name, t.Subject, t.Body, t.IsArchived, t.UpdatedAt))
            .ToListAsync(ct);
    }
}

internal sealed record SaveMailTemplateCommand(Guid? Id, string Name, string Subject, string Body)
    : IRequest<Result<MailTemplateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "MailTemplate";
    public string? ResourceId => Id?.ToString();
}

internal sealed class SaveMailTemplateHandler(IntegrationDbContext db, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<SaveMailTemplateCommand, Result<MailTemplateDto>>
{
    public async Task<Result<MailTemplateDto>> Handle(SaveMailTemplateCommand cmd, CancellationToken ct)
    {
        MailTemplate template;

        if (cmd.Id is { } id)
        {
            var existing = await db.MailTemplates.AsTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (existing is null) return Result.Fail<MailTemplateDto>(MailTemplateRenderer.NotFound);

            var updated = existing.Update(cmd.Name, cmd.Subject, cmd.Body, me.Id, clock.GetUtcNow());
            if (updated.IsFailure) return Result.Fail<MailTemplateDto>(updated.Error!);
            template = existing;
        }
        else
        {
            var created = MailTemplate.Create(me.TenantId, cmd.Name, cmd.Subject, cmd.Body, me.Id, clock.GetUtcNow());
            if (created.IsFailure) return Result.Fail<MailTemplateDto>(created.Error!);
            template = created.Value!;
            db.MailTemplates.Add(template);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new MailTemplateDto(template.Id, template.Name, template.Subject, template.Body, template.IsArchived, template.UpdatedAt));
    }
}

internal sealed record ArchiveMailTemplateCommand(Guid Id, bool Archived) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "MailTemplate";
    public string? ResourceId => Id.ToString();
}

internal sealed class ArchiveMailTemplateHandler(IntegrationDbContext db, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<ArchiveMailTemplateCommand, Result>
{
    public async Task<Result> Handle(ArchiveMailTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.MailTemplates.AsTracking().FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null) return Result.Fail(MailTemplateRenderer.NotFound);

        template.Archive(cmd.Archived, me.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record RenderMailTemplateQuery(Guid TemplateId, RenderMailTemplateRequest Request) : IRequest<Result<RenderedMailTemplateDto>>;

/// <summary>
/// Fills a template for one recipient: <c>{{clé}}</c> → its value, HTML-encoded in the body (a
/// name is text, never markup), as is in the subject. A variable with no value is left blank and
/// NAMED in <see cref="RenderedMailTemplateDto.Missing"/> — « Bonjour , » is better caught by the
/// agent before sending than by the client after.
/// </summary>
internal sealed partial class MailTemplateRenderer(
    IntegrationDbContext db, ICurrentUser me, ITenantStore tenants, TimeProvider clock,
    ILeadsModule? leads = null, ICustomersModule? customers = null)
    : IRequestHandler<RenderMailTemplateQuery, Result<RenderedMailTemplateDto>>
{
    public const string NotFound = "MAIL_TEMPLATE_NOT_FOUND";

    public static readonly IReadOnlyList<MailTemplateVariableDto> Variables =
    [
        new("destinataire.nom", "Nom du destinataire (lead ou client)"),
        new("client.numero", "Numéro client"),
        new("agent.nom", "Nom de l'agent qui écrit"),
        new("agent.email", "Adresse de la boîte qui envoie"),
        new("institution", "Nom de l'institution"),
        new("date", "Date du jour (jj/mm/aaaa)"),
    ];

    [GeneratedRegex(@"\{\{\s*([a-z_.]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public async Task<Result<RenderedMailTemplateDto>> Handle(RenderMailTemplateQuery query, CancellationToken ct)
    {
        var template = await db.MailTemplates.FirstOrDefaultAsync(t => t.Id == query.TemplateId && !t.IsArchived, ct);
        if (template is null) return Result.Fail<RenderedMailTemplateDto>(NotFound);

        var values = await ValuesAsync(query.Request, ct);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        string Fill(string text, bool html) => Placeholder().Replace(text, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return html ? Escape(value) : value;

            missing.Add(key);
            return string.Empty;
        });

        return Result.Ok(new RenderedMailTemplateDto(Fill(template.Subject, html: false), Fill(template.Body, html: true), missing.ToList()));
    }

    /// <summary>
    /// The five characters that make text markup, and only those: <c>WebUtility.HtmlEncode</c> also
    /// turns « é » into <c>&amp;#233;</c>, which the agent then sees and edits in the composer.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private async Task<Dictionary<string, string?>> ValuesAsync(RenderMailTemplateRequest request, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["agent.nom"] = me.DisplayName,
            ["date"] = clock.GetUtcNow().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["institution"] = (await tenants.GetAsync(me.TenantId, ct))?.Name,
        };

        if (request.AccountId is { } accountId)
            values["agent.email"] = await db.ConnectedAccounts.Where(a => a.Id == accountId && a.UserId == me.Id).Select(a => a.Email).FirstOrDefaultAsync(ct);

        // The ids come from the browser: a name or a client number is read only for a record the
        // caller may open — the record's own perimeter, asked of its module. Outside it, the
        // variables stay empty and are reported missing, exactly as for a record with no value.
        if (request.ClientId is { } clientId && customers is not null
            && await customers.CanActorAccessClientAsync(me.TenantId, clientId, me.Id, ct)
            && await customers.GetClientSummaryAsync(me.TenantId, clientId, ct) is { } client)
        {
            values["destinataire.nom"] = client.DisplayName;
            values["client.numero"] = client.ClientNumber;
        }
        else if (request.LeadId is { } leadId && leads is not null
            && await leads.CanActorWorkLeadAsync(me.TenantId, leadId, me.Id, ct)
            && await leads.GetLeadAsync(leadId, ct) is { } lead)
        {
            values["destinataire.nom"] = lead.FullName;
        }

        return values;
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

internal static class ConnectedMailboxSendEndpoints
{
    internal static IEndpointRouteBuilder MapConnectedMailboxSendEndpoints(this IEndpointRouteBuilder app)
    {
        var mail = app.MapGroup("integration/me/mail")
            .WithTags("ConnectedMailbox")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code);

        mail.MapPost("send", SendAsync)
            .WithName("SendMail")
            .WithSummary("Sends an e-mail from one of the caller's mailboxes — new, reply or forward — with attachments")
            .WithDescription(
                "multipart/form-data. `to`, `cc`, `bcc`: repeated fields, or one field separated by commas. `body`: HTML. "
                + "`replyToMessageId`: a kept message of the same mailbox — the reply is threaded. `forwardMessageId` + repeated "
                + "`forwardAttachmentIds`: the original's attachments, fetched now. `files`: up to 10 files, 20 MB in all, "
                + "executables refused. Answers 202: the provider accepted it; it appears in the conversation after the sync "
                + "that follows, when a lead or a client is among its recipients. Limited to 30 sends a minute per user. "
                + "`sendAt`: « Programmer l'envoi » — everything is checked now and the message is kept, encrypted, to leave "
                + "at that instant; the answer carries its `scheduledId`.")
            .Accepts<SendMailForm>("multipart/form-data")
            .DisableAntiforgery()
            .RequireRateLimiting("mail-send")
            .Produces<SendMailOutcome>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status502BadGateway)
            .WithOpenApi()
            .WithTenantHeader();

        mail.MapPut("messages/{id:guid}/thread/read", async (Guid id, SetMailThreadReadRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetMailThreadReadCommand(id, req.IsRead), ct)))
            .WithName("SetMailThreadRead")
            .WithSummary("Marks the whole conversation read or unread — at the provider, then here")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        mail.MapGet("messages/{id:guid}/attachments/{attachmentId}", async (Guid id, string attachmentId, HttpContext http, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetMailAttachmentQuery(id, attachmentId), ct);
                if (result.IsFailure) return Problem(result.Error!);

                // A client's document, behind a permission that may be withdrawn tomorrow: never cached.
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(result.Value!.Content, result.Value.ContentType, result.Value.Name);
            })
            .WithName("GetMailAttachment")
            .WithSummary("Opens an attachment of one of the caller's kept messages, fetched from the provider now")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        mail.MapPost("templates/{id:guid}/render", async (Guid id, RenderMailTemplateRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RenderMailTemplateQuery(id, req), ct)))
            .WithName("RenderMailTemplate")
            .WithSummary("Fills a template for one lead or client; the variables with no value are listed in `missing`")
            .Produces<RenderedMailTemplateDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        var templates = app.MapGroup("integration/mail-templates").WithTags("MailTemplates");

        templates.MapGet("", async (bool? includeArchived, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListMailTemplatesQuery(includeArchived ?? false), ct)))
            .WithName("ListMailTemplates")
            .WithSummary("The institution's e-mail templates; archived ones only for whoever may write them")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code)
            .Produces<IReadOnlyList<MailTemplateDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        templates.MapGet("variables", () => Results.Ok(MailTemplateRenderer.Variables))
            .WithName("ListMailTemplateVariables")
            .WithSummary("The variables a template may use, written {{clé}}")
            .RequireAuthorization(Permissions.CanUseConnectedAccounts.Code)
            .Produces<IReadOnlyList<MailTemplateVariableDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        templates.MapPost("", async (SaveMailTemplateRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SaveMailTemplateCommand(null, req.Name, req.Subject, req.Body), ct)))
            .WithName("CreateMailTemplate")
            .RequireAuthorization(Permissions.CanManageMailTemplates.Code)
            .Produces<MailTemplateDto>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        templates.MapPut("{id:guid}", async (Guid id, SaveMailTemplateRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SaveMailTemplateCommand(id, req.Name, req.Subject, req.Body), ct)))
            .WithName("UpdateMailTemplate")
            .RequireAuthorization(Permissions.CanManageMailTemplates.Code)
            .Produces<MailTemplateDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        templates.MapPut("{id:guid}/archive", async (Guid id, ArchiveMailTemplateRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new ArchiveMailTemplateCommand(id, req.Archived), ct)))
            .WithName("ArchiveMailTemplate")
            .WithSummary("Archives a template (or brings it back); it is never deleted")
            .RequireAuthorization(Permissions.CanManageMailTemplates.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    /// <summary>
    /// The form is read by hand: minimal-API binding refuses <c>[FromForm] string[]</c> AT START-UP
    /// (« TryParse method found on string with incorrect format ») — the whole host failed to
    /// build its endpoints. <see cref="SendMailForm"/> documents the fields for the OpenAPI document.
    /// </summary>
    private static async Task<IResult> SendAsync(HttpRequest request, ISender sender, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Problem(MailSendErrors.RecipientsRequired);

        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["accountId"], out var accountId)) return Problem(ConnectedAccountErrors.NotFound);

        static Guid? OptionalGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
        static string[] All(Microsoft.Extensions.Primitives.StringValues values) => values.OfType<string>().ToArray();

        // Refused before reading: the sizes are known from the request, the bytes are not needed.
        var forwardIds = All(form["forwardAttachmentIds"]);
        var check = SendMailHandler.CheckAttachments(form.Files.Select(f => (f.FileName, f.Length)), forwardIds.Length);
        if (check.IsFailure) return Problem(check.Error!);

        var attachments = new List<OutgoingAttachment>(form.Files.Count);
        foreach (var file in form.Files)
        {
            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            attachments.Add(new OutgoingAttachment(
                Path.GetFileName(file.FileName), string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, buffer.ToArray()));
        }

        DateTimeOffset? sendAt = DateTimeOffset.TryParse(form["sendAt"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        if (!string.IsNullOrWhiteSpace(form["sendAt"]) && sendAt is null) return Problem(MailSendErrors.ScheduleInvalid);

        var result = await sender.Send(new SendMailCommand(
            accountId, All(form["to"]), All(form["cc"]), All(form["bcc"]), form["subject"].ToString(), form["body"].ToString(),
            OptionalGuid(form["replyToMessageId"]), OptionalGuid(form["forwardMessageId"]), forwardIds, attachments,
            OptionalGuid(form["toClientId"]), sendAt), ct);

        return result.IsSuccess ? Results.Accepted(value: result.Value) : Problem(result.Error!);
    }

    private static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        ConnectedAccountErrors.NotFound or MailSendErrors.MessageNotFound or MailSendErrors.AttachmentNotFound or MailTemplateRenderer.NotFound
            => Results.Problem(title: "Messagerie", detail: code, statusCode: StatusCodes.Status404NotFound),
        ConnectedAccountErrors.NeedsReconnect or MailSendErrors.MailboxDisabled
            => Results.Problem(title: "Messagerie", detail: code, statusCode: StatusCodes.Status409Conflict),
        MailSendException.Failed or ConnectedAccountErrors.ProviderError
            => Results.Problem(title: "Messagerie", detail: code, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Problem(title: "Messagerie", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}

public sealed record SetMailThreadReadRequest(bool IsRead);

/// <summary>The fields of <c>POST integration/me/mail/send</c>, for the OpenAPI document — the endpoint reads the form itself.</summary>
public sealed class SendMailForm
{
    public Guid AccountId { get; set; }
    public List<string>? To { get; set; }
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
    public string? Subject { get; set; }

    /// <summary>HTML.</summary>
    public string? Body { get; set; }

    public Guid? ReplyToMessageId { get; set; }
    public Guid? ForwardMessageId { get; set; }
    public List<string>? ForwardAttachmentIds { get; set; }
    public List<IFormFile>? Files { get; set; }

    /// <summary>
    /// A client to write to, by record: the server reads their address (the agent's screen shows it
    /// masked) and adds it to « À ».
    /// </summary>
    public Guid? ToClientId { get; set; }

    /// <summary>
    /// « Programmer l'envoi » : the instant the message leaves (ISO 8601, with its offset). Between
    /// a minute and a year from now; absent, it leaves now.
    /// </summary>
    public DateTimeOffset? SendAt { get; set; }
}
