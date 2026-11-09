namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// What an agent does with a conversation (WA-BE-16, WA-BE-17, WA-FE-08): take it over, answer
/// freely — text, image or PDF — hand it back to the bot, close it; and what a supervisor adds:
/// hand it to somebody else. Every one is an <c>ICommand</c>, so the audit trail records who
/// took over and who reassigned, as WA-BE-20 asks; a message's text is marked sensitive and only
/// its field name reaches the audit row.
/// </summary>
internal static class WhatsAppConversationErrors
{
    public const string ConversationNotFound = "WHATSAPP_CONVERSATION_NOT_FOUND";
    public const string NotHolder = "WHATSAPP_NOT_HOLDER";
    public const string HeldByAnother = "WHATSAPP_CONVERSATION_HELD_BY_ANOTHER";
    public const string WindowClosed = "WHATSAPP_WINDOW_CLOSED";
    public const string TextInvalid = "WHATSAPP_TEXT_INVALID";
    public const string MediaInvalid = "WHATSAPP_MEDIA_INVALID";
    /// <summary>Not an image (JPEG, PNG) nor a PDF: WhatsApp does not carry it — by e-mail only.</summary>
    public const string MediaTypeNotAllowed = "WHATSAPP_MEDIA_TYPE_NOT_ALLOWED";
    public const string AgentNotInTeam = "WHATSAPP_AGENT_NOT_IN_TEAM";
    public const string NotSupervisor = "WHATSAPP_NOT_SUPERVISOR";
}

/// <summary>Loads a conversation of a lead, past the read rule of the thread.</summary>
internal sealed class WhatsAppConversationLoader(LeadsDbContext db, WhatsAppLeadAccess access)
{
    public async Task<Result<(Lead Lead, WhatsAppConversation Conversation)>> LoadAsync(
        Guid leadId, Guid conversationId, bool hasPermission, CancellationToken ct)
    {
        var lead = await access.LoadAsync(leadId, hasPermission, ct);
        if (lead.IsFailure) return Result.Fail<(Lead, WhatsAppConversation)>(lead.Error!);

        var conversation = await db.WhatsAppConversations
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.LeadId == leadId, ct);

        return conversation is null
            ? Result.Fail<(Lead, WhatsAppConversation)>(WhatsAppConversationErrors.ConversationNotFound)
            : Result.Ok((lead.Value!, conversation));
    }
}

// ── Take over ────────────────────────────────────────────────────────────────────────────────

internal sealed record TakeOverWhatsAppCommand(Guid LeadId, Guid ConversationId, bool MayReply)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

internal sealed class TakeOverWhatsAppHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me, IRealtimeNotifier realtime,
    TimeProvider clock, ILogger<TakeOverWhatsAppHandler> logger)
    : IRequestHandler<TakeOverWhatsAppCommand, Result>
{
    public async Task<Result> Handle(TakeOverWhatsAppCommand cmd, CancellationToken ct)
    {
        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, cmd.MayReply, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);

        var (lead, conversation) = loaded.Value;
        var expected = conversation.TargetAgentId;

        var taken = conversation.TakeOver(me.Id, clock.GetUtcNow());
        if (taken.IsFailure) return taken;

        await WhatsAppHandovers.EndAsync(db, conversation.Id, WhatsAppHandoverEnd.TakenOver, me.Id, clock.GetUtcNow(), ct);

        conversation.MarkRead();
        await db.SaveChangesAsync(ct);

        await WhatsAppRealtime.PushAsync(
            realtime, logger, me.TenantId, [expected, lead.CurrentAssignedId, lead.OwnerId],
            WhatsAppRealtime.ConversationUpdated, lead.Id, conversation.Id, Guid.Empty, ct);

        return Result.Ok();
    }
}

// ── Reply: text ──────────────────────────────────────────────────────────────────────────────

public sealed record SendWhatsAppTextRequest(string Text);

internal sealed record SendWhatsAppTextCommand(
    Guid LeadId, Guid ConversationId, [property: SensitiveData] string Text, bool MayReply)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

/// <summary>
/// A free message (WA-BE-16): only from the agent who holds the conversation, only inside the
/// 24-hour window, never to a customer who opted out. Queued through the outbox like a template.
/// </summary>
internal sealed class SendWhatsAppTextHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock)
    : IRequestHandler<SendWhatsAppTextCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SendWhatsAppTextCommand cmd, CancellationToken ct)
    {
        var text = cmd.Text?.Trim() ?? string.Empty;

        if (text.Length is 0 or > WhatsAppMessage.BodyMaxLength)
            return Result.Fail<Guid>(WhatsAppConversationErrors.TextInvalid);

        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, cmd.MayReply, ct);
        if (loaded.IsFailure) return Result.Fail<Guid>(loaded.Error!);

        var (lead, conversation) = loaded.Value;
        var now = clock.GetUtcNow();

        var refusal = await ReplyRules.CheckAsync(db, lead, conversation, me.Id, now, ct);
        if (refusal is not null) return Result.Fail<Guid>(refusal);

        var message = WhatsAppMessage.OutboundText(me.TenantId, conversation.Id, WhatsAppAuthor.Agent, me.Id, text, now);
        db.WhatsAppMessages.Add(message);
        conversation.RecordOutbound(now);

        await publisher.PublishAsync(new WhatsAppMessageQueuedEvent(me.TenantId, message.Id), ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok(message.Id);
    }
}

/// <summary>The three conditions of a free message, shared by text and media.</summary>
internal static class ReplyRules
{
    public static async Task<string?> CheckAsync(
        LeadsDbContext db, Lead lead, WhatsAppConversation conversation, Guid me, DateTimeOffset now, CancellationToken ct)
    {
        if (conversation.Status != WhatsAppConversationStatus.Human || conversation.OwnerUserId != me)
            return WhatsAppConversationErrors.NotHolder;

        if (!conversation.IsWindowOpen(now))
            return WhatsAppConversationErrors.WindowClosed;

        // An opt-out wins over an open window: the customer wrote STOP, not « write to me ».
        var consents = await db.LeadConsents
            .Where(c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null)
            .ToListAsync(ct);

        return WhatsAppEligibility.ConsentStateOf(consents) == WhatsAppConsentState.Revoked
            ? WhatsAppErrors.ConsentRevoked
            : null;
    }
}

// ── Reply: media ─────────────────────────────────────────────────────────────────────────────

/// <param name="Content">The file's bytes. Marked sensitive: never written to the audit trail.</param>
internal sealed record SendWhatsAppMediaCommand(
    Guid LeadId,
    Guid ConversationId,
    [property: SensitiveData] byte[] Content,
    string ContentType,
    [property: SensitiveData] string FileName,
    [property: SensitiveData] string? Caption,
    bool MayReply)
    : IRequest<Result<SendWhatsAppMediaResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

/// <param name="Status"><c>Sent</c>, or <c>Failed</c> with <paramref name="ErrorCode"/>.</param>
public sealed record SendWhatsAppMediaResult(Guid MessageId, string Status, string? ErrorCode);

/// <summary>
/// An image or a PDF (WA-BE-16), sent while the agent waits: the bytes are in this request and
/// are stored nowhere, so they cannot be handed to a consumer later. The sending itself is
/// <see cref="WhatsAppMediaSender"/>, shared with the client's and the project's routes.
/// </summary>
internal sealed class SendWhatsAppMediaHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me, WhatsAppMediaSender sender, TimeProvider clock)
    : IRequestHandler<SendWhatsAppMediaCommand, Result<SendWhatsAppMediaResult>>
{
    internal const int MaxImageBytes = WhatsAppMediaSender.MaxImageBytes;
    internal const int MaxDocumentBytes = WhatsAppMediaSender.MaxDocumentBytes;

    public async Task<Result<SendWhatsAppMediaResult>> Handle(SendWhatsAppMediaCommand cmd, CancellationToken ct)
    {
        var media = WhatsAppMediaSender.Check(cmd.Content, cmd.ContentType);
        if (media.IsFailure) return Result.Fail<SendWhatsAppMediaResult>(WhatsAppConversationErrors.MediaInvalid);

        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, cmd.MayReply, ct);
        if (loaded.IsFailure) return Result.Fail<SendWhatsAppMediaResult>(loaded.Error!);

        var (lead, conversation) = loaded.Value;
        var refusal = await ReplyRules.CheckAsync(db, lead, conversation, me.Id, clock.GetUtcNow(), ct);
        if (refusal is not null) return Result.Fail<SendWhatsAppMediaResult>(refusal);

        return Result.Ok(await sender.SendAsync(conversation, media.Value!, cmd.Content, cmd.FileName, cmd.Caption, link: null, ct));
    }

    internal static bool MatchesSignature(byte[] content, string type) => WhatsAppMediaSender.MatchesSignature(content, type);
}

/// <summary>What a file is to WhatsApp: its kind and its normalised type.</summary>
internal sealed record WhatsAppMediaKind(WhatsAppMessageKind Kind, string ContentType);

/// <summary>
/// Sends one image or PDF in a conversation the caller may write in — the checks of WHO may are
/// the caller's (lead, client or project route). The provider call runs with the ambient
/// transaction suppressed: it reaches another module's store for the number's key. A failure the
/// provider reports is a FAILED message, recorded and answered, never an error.
/// </summary>
internal sealed class WhatsAppMediaSender(
    LeadsDbContext db, ICurrentUser me, IIntegrationModule integration, TimeProvider clock,
    IServiceProvider services, ILogger<WhatsAppMediaSender> logger)
{
    /// <summary>Meta's own ceiling for an image is 5 MB; a document is held to the platform's 10 MB.</summary>
    internal const int MaxImageBytes = 5 * 1024 * 1024;
    internal const int MaxDocumentBytes = 10 * 1024 * 1024;

    /// <summary>
    /// JPEG and PNG as images, PDF as a document, within their size, and bytes that ARE what the type
    /// says. Anything else — a Word file, a spreadsheet — cannot go by WhatsApp: by e-mail only.
    /// </summary>
    public static Result<WhatsAppMediaKind> Check(byte[]? content, string? contentType)
    {
        var type = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

        WhatsAppMessageKind? kind = type switch
        {
            "image/jpeg" or "image/png" => WhatsAppMessageKind.Image,
            "application/pdf" => WhatsAppMessageKind.Document,
            _ => null,
        };
        if (kind is null) return Result.Fail<WhatsAppMediaKind>(WhatsAppConversationErrors.MediaTypeNotAllowed);

        var limit = kind == WhatsAppMessageKind.Image ? MaxImageBytes : MaxDocumentBytes;
        if (content is not { Length: > 0 } || content.Length > limit || !MatchesSignature(content, type))
            return Result.Fail<WhatsAppMediaKind>(WhatsAppConversationErrors.MediaInvalid);

        return Result.Ok(new WhatsAppMediaKind(kind.Value, type));
    }

    public async Task<SendWhatsAppMediaResult> SendAsync(
        WhatsAppConversation conversation, WhatsAppMediaKind media, byte[] content, string? fileName, string? caption,
        Action<WhatsAppMessage>? link, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var name = SafeFileName(fileName, media.ContentType);
        var text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();

        var message = WhatsAppMessage.OutboundMedia(me.TenantId, conversation.Id, me.Id, media.Kind, media.ContentType, name, text, now);
        link?.Invoke(message);
        db.WhatsAppMessages.Add(message);
        conversation.RecordOutbound(now);

        IntegrationResult<MessagingMediaSent> sent;

        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            sent = await integration.Messaging.SendMediaAsync(
                me.TenantId, conversation.ConnectionId, conversation.ContactPhone,
                new MessagingMediaContent(content, media.ContentType, name), text, ct);
            suppressed.Complete();
        }

        if (sent.IsSuccess)
        {
            message.AttachProviderMedia(sent.Value.MediaId);
            message.MarkSent(sent.Value.ProviderMessageId, clock.GetUtcNow());
            await KeepCopyAsync(message, content, ct);
        }
        else
        {
            message.MarkFailed(sent.Code ?? WhatsAppErrors.SendFailed, sent.Detail, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);

        return new SendWhatsAppMediaResult(message.Id, message.Status.ToString(), message.ErrorCode);
    }

    /// <summary>
    /// Our encrypted copy of what was sent (WA-BE-20) — the bytes exist only in this request, so
    /// it is now or never. A store that fails costs the copy, never the send already made.
    /// </summary>
    private async Task KeepCopyAsync(WhatsAppMessage message, byte[] content, CancellationToken ct)
    {
        if (services.GetService(typeof(Infrastructure.WhatsAppMedia.IWhatsAppMediaStore)) is not Infrastructure.WhatsAppMedia.IWhatsAppMediaStore store)
            return;

        try
        {
            var stored = await store.StoreAsync(me.TenantId, content, ct);
            message.AttachStoredMedia(stored.StorageRef, stored.SizeBytes, stored.Sha256, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The copy of WhatsApp media {MessageId} could not be stored.", message.Id);
        }
    }

    /// <summary>
    /// The bytes must be what the type says: a renamed executable is refused here, not by the
    /// customer's phone. JPEG FF D8 FF, PNG 89 50 4E 47, PDF %PDF.
    /// </summary>
    internal static bool MatchesSignature(byte[] content, string type) => type switch
    {
        "image/jpeg" => content.Length > 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
        "image/png" => content.Length > 4 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47,
        "application/pdf" => content.Length > 4 && content[0] == 0x25 && content[1] == 0x50 && content[2] == 0x44 && content[3] == 0x46,
        _ => false,
    };

    internal static string SafeFileName(string? name, string type)
    {
        var fallback = type == "application/pdf" ? "document.pdf" : type == "image/png" ? "image.png" : "image.jpg";
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var cleaned = new string(Path.GetFileName(name).Where(c => !char.IsControl(c) && c is not ('/' or '\\' or '"')).ToArray()).Trim();
        return cleaned.Length is 0 or > 120 ? fallback : cleaned;
    }
}

// ── Return to the bot, close ─────────────────────────────────────────────────────────────────

internal sealed record ReturnWhatsAppToBotCommand(Guid LeadId, Guid ConversationId, bool MayReply)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

internal sealed class ReturnWhatsAppToBotHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<ReturnWhatsAppToBotCommand, Result>
{
    public async Task<Result> Handle(ReturnWhatsAppToBotCommand cmd, CancellationToken ct)
    {
        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, cmd.MayReply, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);

        var returned = loaded.Value.Conversation.ReturnToBot(me.Id);
        if (returned.IsFailure) return returned;

        await WhatsAppHandovers.EndAsync(db, cmd.ConversationId, WhatsAppHandoverEnd.ReturnedToBot, null, clock.GetUtcNow(), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed record CloseWhatsAppConversationRequest(string Reason);

internal sealed record CloseWhatsAppConversationCommand(
    Guid LeadId, Guid ConversationId, [property: SensitiveData] string Reason, bool MayReply, bool MaySupervise)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

/// <summary>
/// Closed with its reason (WA-BE-17) — by its holder, or by a supervisor. Another agent's
/// conversation is not closed from under them.
/// </summary>
internal sealed class CloseWhatsAppConversationHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<CloseWhatsAppConversationCommand, Result>
{
    public async Task<Result> Handle(CloseWhatsAppConversationCommand cmd, CancellationToken ct)
    {
        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, cmd.MayReply || cmd.MaySupervise, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);

        var conversation = loaded.Value.Conversation;

        if (conversation.Status == WhatsAppConversationStatus.Human
            && conversation.OwnerUserId != me.Id
            && !cmd.MaySupervise)
        {
            return Result.Fail(WhatsAppConversationErrors.HeldByAnother);
        }

        var closed = conversation.Close(cmd.Reason ?? string.Empty, me.Id, clock.GetUtcNow());
        if (closed.IsFailure) return closed;

        await WhatsAppHandovers.EndAsync(db, conversation.Id, WhatsAppHandoverEnd.Closed, null, clock.GetUtcNow(), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Reassign (supervisor) ────────────────────────────────────────────────────────────────────

public sealed record ReassignWhatsAppConversationRequest(Guid AgentId);

internal sealed record ReassignWhatsAppConversationCommand(Guid LeadId, Guid ConversationId, Guid AgentId, bool MaySupervise)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

/// <summary>
/// A supervisor hands the conversation to an agent of their team (WA-FE-08). The only way to
/// take a conversation from the agent holding it — the one-holder rule of WA-BE-16.
/// </summary>
internal sealed class ReassignWhatsAppConversationHandler(
    LeadsDbContext db, WhatsAppConversationLoader loader, ICurrentUser me, IAdministrationModule admin,
    ILeadSettings settings, IRealtimeNotifier realtime, TimeProvider clock, ILogger<ReassignWhatsAppConversationHandler> logger)
    : IRequestHandler<ReassignWhatsAppConversationCommand, Result>
{
    public async Task<Result> Handle(ReassignWhatsAppConversationCommand cmd, CancellationToken ct)
    {
        if (!cmd.MaySupervise) return Result.Fail(WhatsAppConversationErrors.NotSupervisor);

        var loaded = await loader.LoadAsync(cmd.LeadId, cmd.ConversationId, hasPermission: true, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);

        var (lead, conversation) = loaded.Value;

        // Within the supervisor's own team: a supervisor of one branch must not push work onto
        // another branch's agents. The bypass roles of LeadAccessGuard see the whole tenant.
        if (!LeadAccessGuard.HasBypassRole(me))
        {
            var team = await admin.GetTeamAgentIdsAsync(me.TenantId, me.Id, ct);
            if (!team.Contains(cmd.AgentId)) return Result.Fail(WhatsAppConversationErrors.AgentNotInTeam);
        }

        var previous = new[] { conversation.OwnerUserId, conversation.TargetAgentId };
        var minutes = await settings.GetIntAsync(me.TenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, ct);

        var reassigned = conversation.Reassign(cmd.AgentId, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null, clock.GetUtcNow());
        if (reassigned.IsFailure) return reassigned;

        if (conversation.Status == WhatsAppConversationStatus.PendingHuman
            && await WhatsAppHandovers.FindOpenAsync(db, conversation.Id, ct) is { } open)
            open.MoveDeadline(conversation.TakeoverDeadline);

        await db.SaveChangesAsync(ct);

        await WhatsAppRealtime.PushAsync(
            realtime, logger, me.TenantId, [.. previous, cmd.AgentId],
            WhatsAppRealtime.ConversationUpdated, lead.Id, conversation.Id, Guid.Empty, ct);

        return Result.Ok();
    }
}
