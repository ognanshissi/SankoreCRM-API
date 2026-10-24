namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Everything the lead record's WhatsApp tab and its button need, in one read (WA-FE-03, WA-FE-04,
/// WA-FE-06).
/// </summary>
/// <param name="ConsentState"><c>Unknown</c>, <c>Granted</c> or <c>Revoked</c>.</param>
/// <param name="MaskedPhone">The lead's number as « ••1801 », or null when it cannot be written in E.164.</param>
/// <param name="Blockers">
/// Why a template cannot be sent now, as error codes — empty when it can. The same evaluation the
/// send runs, so the button never offers what the server would refuse.
/// </param>
/// <param name="MayForce">The caller may send again before 24 hours without an answer.</param>
public sealed record WhatsAppPanelDto(
    string ConsentState,
    DateTimeOffset? ConsentAt,
    string? ConsentChannel,
    string? ConsentProof,
    string? MaskedPhone,
    WhatsAppChannelSummaryDto? Channel,
    bool CanSend,
    IReadOnlyList<string> Blockers,
    bool MayForce,
    WhatsAppConversationSummaryDto? Conversation,
    bool MayReply = false,
    bool MaySupervise = false);

public sealed record WhatsAppChannelSummaryDto(Guid ConnectionId, string? DisplayPhoneNumber, bool IsActive);

/// <param name="Status"><c>AwaitingReply</c>, <c>Bot</c>, <c>PendingHuman</c>, <c>Human</c> or <c>Closed</c>.</param>
/// <param name="NextTemplateAllowedAt">When another template may be sent without forcing; null when now.</param>
/// <param name="AmbiguousMatch">The number matched several records and this lead was chosen.</param>
/// <param name="IsHolder">The caller holds it — the composer is theirs.</param>
/// <param name="CanReply">
/// A free message may be sent now by the caller: they hold it, the window is open, the customer
/// did not opt out, and they have <c>WhatsApp.Reply</c>.
/// </param>
/// <param name="EscalationReason">A code — <c>CUSTOMER_REPLIED</c>, <c>REASSIGNED</c>, a bot reason from lot 3.</param>
public sealed record WhatsAppConversationSummaryDto(
    Guid Id,
    string Status,
    DateTimeOffset? WindowExpiresAt,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset? LastTemplateSentAt,
    DateTimeOffset? NextTemplateAllowedAt,
    int UnreadCount,
    bool AmbiguousMatch,
    string? ContactName,
    Guid? OwnerUserId = null,
    string? OwnerName = null,
    Guid? TargetAgentId = null,
    string? TargetAgentName = null,
    DateTimeOffset? PendingSince = null,
    DateTimeOffset? TakeoverDeadline = null,
    bool TakeoverBreached = false,
    string? EscalationReason = null,
    string? EscalationSummary = null,
    string? CloseReason = null,
    bool IsHolder = false,
    bool CanReply = false,
    string? EscalationReasonLabel = null,
    IReadOnlyList<WhatsAppBotCollectedFieldDto>? BotCollectedFields = null,
    string? MaskedPhone = null);

/// <summary>
/// A lead field the bot filled in during its session (WA-FE-12), with what the record says NOW —
/// the value is read off the lead, so an agent's correction since shows here too.
/// </summary>
/// <param name="Code"><c>FIRST_NAME</c>, <c>LAST_NAME</c>, <c>EMAIL</c>, <c>CITY</c> or <c>DESIRED_AMOUNT</c>.</param>
public sealed record WhatsAppBotCollectedFieldDto(string Code, string Label, string? Value);

/// <summary>One bubble of the thread (WA-FE-05).</summary>
/// <param name="Author"><c>Contact</c>, <c>Agent</c> or <c>Bot</c>.</param>
/// <param name="Status"><c>Queued</c>, <c>Sending</c>, <c>Sent</c>, <c>Delivered</c>, <c>Read</c>, <c>Failed</c> or <c>Received</c>.</param>
public sealed record WhatsAppMessageDto(
    Guid Id,
    Guid ConversationId,
    string Direction,
    string Author,
    Guid? AuthorUserId,
    string? AuthorName,
    string Kind,
    string? Body,
    string? TemplateName,
    bool HasMedia,
    string? MediaMimeType,
    string? MediaFileName,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? FailedAt,
    string? ErrorCode,
    string? ErrorDetail,
    bool MediaPurged = false);

/// <param name="Items">Newest first. The screen reverses them.</param>
/// <param name="NextBefore">The cursor for older messages; null when there are none.</param>
public sealed record WhatsAppMessagePageDto(IReadOnlyList<WhatsAppMessageDto> Items, DateTimeOffset? NextBefore);

/// <summary>A template the agent may send to this lead, with its variables already filled (WA-FE-04).</summary>
public sealed record WhatsAppComposeTemplateDto(
    Guid Id,
    string Name,
    string Language,
    string Category,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons,
    IReadOnlyList<WhatsAppComposeVariableDto> Variables);

/// <param name="Field">The lead field it was filled from, or null when the agent must type it.</param>
public sealed record WhatsAppComposeVariableDto(int Index, string? Field, string Value);

// ── Shared access ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Who may READ a lead's WhatsApp exchange (WA-BE-19): the holder of <c>WhatsApp.Send</c> or
/// <c>WhatsApp.Supervise</c>, read from the token by the endpoint, AND somebody the lead's own
/// ownership rule lets work it — its assignee, owner, collector, or their supervisor. The thread
/// is decrypted only past both, which is the « uniquement pour les utilisateurs autorisés » of the
/// specification.
/// </summary>
internal sealed class WhatsAppLeadAccess(LeadsDbContext db, LeadAccessGuard guard)
{
    public async Task<Result<Lead>> LoadAsync(Guid leadId, bool hasWhatsAppPermission, CancellationToken ct)
    {
        if (!hasWhatsAppPermission) return Result.Fail<Lead>(LeadAccessGuard.Forbidden);

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null || lead.IsAnonymized) return Result.Fail<Lead>(WhatsAppErrors.LeadNotFound);

        var access = await guard.EnsureCanWorkAsync(lead, ct);
        return access.IsFailure ? Result.Fail<Lead>(access.Error!) : Result.Ok(lead);
    }
}

// ── Panel ────────────────────────────────────────────────────────────────────────────────────

internal sealed record GetWhatsAppPanelQuery(
    Guid LeadId, bool HasWhatsAppPermission, bool MayForce, bool MayReply = false, bool MaySupervise = false)
    : ILeadReadRequest, IRequest<Result<WhatsAppPanelDto>>;

internal sealed class GetWhatsAppPanelHandler(
    LeadsDbContext db,
    WhatsAppLeadAccess access,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    IIntegrationModule integration,
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer blindIndexer,
    Bot.WhatsAppBotFieldValues fieldValues,
    TimeProvider clock)
    : IRequestHandler<GetWhatsAppPanelQuery, Result<WhatsAppPanelDto>>
{
    public async Task<Result<WhatsAppPanelDto>> Handle(GetWhatsAppPanelQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.LeadId, query.HasWhatsAppPermission, ct);
        if (loaded.IsFailure) return Result.Fail<WhatsAppPanelDto>(loaded.Error!);

        var lead = loaded.Value!;
        var tenantId = currentUser.TenantId;
        var now = clock.GetUtcNow();

        var consents = await db.LeadConsents
            .Where(c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null)
            .ToListAsync(ct);

        var state = WhatsAppEligibility.ConsentStateOf(consents);

        var decisive = state == WhatsAppConsentState.Granted
            ? consents.Where(c => c.Status == ConsentStatus.Active).MaxBy(c => c.GrantedAt)
            : consents.MaxBy(c => c.WithdrawnAt ?? c.GrantedAt);

        var callingCode = (await admin.GetSmsConfigAsync(tenantId, ct))?.DefaultCallingCode;
        var channel = await integration.Messaging.ResolveChannelAsync(tenantId, lead.AgencyId, ct);
        var phone = WhatsAppNumbers.ToE164(lead.PhoneNumber, callingCode);

        WhatsAppConversation? conversation = null;

        if (channel is not null && phone is not null)
        {
            var index = blindIndexer.Compute(BlindIndexPurpose.Phone, phone);
            conversation = await db.WhatsAppConversations
                .FirstOrDefaultAsync(c => c.ConnectionId == channel.ConnectionId && c.ContactPhoneBlindIndex == index, ct);
        }

        // A conversation filed on THIS lead through another number — a second handset, a number
        // changed since, a conversation an agent attached from the inbox — is this lead's too. The
        // tab presents one: an OPEN one first (somebody may be waiting on it), then the most
        // recently active. Presenting the lead's own number regardless left an attached
        // conversation waiting for an agent with no screen to take it from, and presenting the
        // latest regardless hid a held conversation behind a closed one.
        var filedOnLead = await db.WhatsAppConversations
            .Where(c => c.LeadId == lead.Id)
            .ToListAsync(ct);

        conversation = filedOnLead
            .Concat(conversation is null ? [] : [conversation])
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.Status == WhatsAppConversationStatus.Closed)
            .ThenByDescending(c => c.LastMessageAt)
            .FirstOrDefault();

        var blockers = WhatsAppEligibility.Blockers(
            lead, state, phone, channel is not null, conversation, query.MayForce, now);

        var people = conversation is null
            ? []
            : new[] { conversation.OwnerUserId, conversation.TargetAgentId }.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();

        var names = people.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenantId, people, ct);

        var isHolder = conversation is { Status: WhatsAppConversationStatus.Human } && conversation.OwnerUserId == currentUser.Id;

        // The catalogue and the lead form name a product and a custom field — read only when the
        // handover block lists one of them.
        var botFields = conversation?.BotCollectedFields.Any(c => c == WhatsAppBotFields.Product || WhatsAppBotFields.IsCustom(c)) == true
            ? await fieldValues.ContextAsync(tenantId, ct)
            : null;

        return Result.Ok(new WhatsAppPanelDto(
            ConsentState: state.ToString(),
            ConsentAt: decisive is null ? null : decisive.Status == ConsentStatus.Active ? decisive.GrantedAt : decisive.WithdrawnAt ?? decisive.GrantedAt,
            ConsentChannel: decisive?.Channel.ToString(),
            ConsentProof: decisive?.ProofReference,
            MaskedPhone: phone is null ? null : WhatsAppNumbers.Mask(phone),
            Channel: channel is null ? null : new WhatsAppChannelSummaryDto(channel.ConnectionId, channel.DisplayPhoneNumber, channel.IsActive),
            CanSend: blockers.Count == 0,
            Blockers: blockers,
            MayForce: query.MayForce,
            Conversation: conversation is null ? null : new WhatsAppConversationSummaryDto(
                conversation.Id,
                conversation.Status.ToString(),
                conversation.WindowExpiresAt,
                conversation.LastInboundAt,
                conversation.LastTemplateSentAt,
                conversation.NextTemplateAllowedAt(now),
                conversation.UnreadCount,
                conversation.AmbiguousMatch,
                conversation.ContactName,
                conversation.OwnerUserId,
                conversation.OwnerUserId is { } o ? names.GetValueOrDefault(o) : null,
                conversation.TargetAgentId,
                conversation.TargetAgentId is { } t ? names.GetValueOrDefault(t) : null,
                conversation.PendingSince,
                conversation.TakeoverDeadline,
                conversation.TakeoverBreachedAt is not null,
                conversation.EscalationReason,
                conversation.EscalationSummary,
                conversation.CloseReason,
                isHolder,
                CanReply: isHolder && query.MayReply && conversation.IsWindowOpen(now) && state != WhatsAppConsentState.Revoked,
                EscalationReasonLabel: conversation.EscalationReason is null ? null : Bot.WhatsAppBotReasons.Label(conversation.EscalationReason),
                BotCollectedFields: conversation.BotCollectedFields
                    .Select(code => new WhatsAppBotCollectedFieldDto(
                        code,
                        botFields?.LabelOf(code) ?? WhatsAppBotFields.LabelOf(code),
                        Bot.WhatsAppBotFieldValues.Display(lead, new Dictionary<string, string>(), code, botFields)))
                    .ToList(),
                // The number THIS conversation runs on — not necessarily the lead's (an attached
                // conversation, a second handset): a reply goes here, so this is what is shown.
                MaskedPhone: conversation.AnonymizedAt is null ? WhatsAppNumbers.Mask(conversation.ContactPhone) : null),
            MayReply: query.MayReply,
            MaySupervise: query.MaySupervise));
    }
}

// ── Thread ───────────────────────────────────────────────────────────────────────────────────

internal sealed record ListWhatsAppMessagesQuery(
    Guid LeadId, bool HasWhatsAppPermission, DateTimeOffset? Before, int Limit)
    : ILeadReadRequest, IRequest<Result<WhatsAppMessagePageDto>>;

/// <summary>
/// The thread, newest first, by a cursor on the message's instant (WA-FE-05 « chargement au
/// défilement »). Every conversation filed on the lead, merged — a lead reached on two numbers
/// has one history.
/// </summary>
internal sealed class ListWhatsAppMessagesHandler(
    LeadsDbContext db,
    WhatsAppLeadAccess access,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    WhatsAppAudit audit)
    : IRequestHandler<ListWhatsAppMessagesQuery, Result<WhatsAppMessagePageDto>>
{
    public async Task<Result<WhatsAppMessagePageDto>> Handle(ListWhatsAppMessagesQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.LeadId, query.HasWhatsAppPermission, ct);
        if (loaded.IsFailure) return Result.Fail<WhatsAppMessagePageDto>(loaded.Error!);

        var limit = Math.Clamp(query.Limit, 1, 100);

        var conversations = await db.WhatsAppConversations
            .Where(c => c.LeadId == query.LeadId)
            .ToListAsync(ct);

        if (conversations.Count == 0)
            return Result.Ok(new WhatsAppMessagePageDto([], null));

        // WA-BE-20: a supervisor opening somebody else's thread is a consultation the audit trail
        // keeps. The first page only — scrolling back is the same reading, not a new one.
        if (query.Before is null)
        {
            foreach (var conversation in conversations.Where(c => !WhatsAppPerimeter.IsInvolved(currentUser.Id, c, loaded.Value)))
                await audit.ConsultedAsync(currentUser.TenantId, currentUser.Id, conversation.Id, query.LeadId, ct);
        }

        var conversationIds = conversations.Select(c => c.Id).ToList();

        return Result.Ok(await WhatsAppMessagePages.ReadAsync(db, admin, currentUser.TenantId, conversationIds, query.Before, limit, ct));
    }
}

/// <summary>One page of messages, newest first — shared by the lead's thread and the conversation detail.</summary>
internal static class WhatsAppMessagePages
{
    public static async Task<WhatsAppMessagePageDto> ReadAsync(
        LeadsDbContext db, IAdministrationModule admin, Guid tenantId, IReadOnlyCollection<Guid> conversationIds,
        DateTimeOffset? before, int limit, CancellationToken ct)
    {
        var page = await db.WhatsAppMessages
            .Where(m => conversationIds.Contains(m.ConversationId)
                     && (before == null || m.OccurredAt < before))
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.CreatedAt)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var authorIds = page.Where(m => m.AuthorUserId is not null).Select(m => m.AuthorUserId!.Value).Distinct().ToList();

        var names = authorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenantId, authorIds, ct);

        var items = page.Select(m => new WhatsAppMessageDto(
            m.Id,
            m.ConversationId,
            m.Direction.ToString(),
            m.Author.ToString(),
            m.AuthorUserId,
            m.AuthorUserId is { } a && names.TryGetValue(a, out var name) ? name : null,
            m.Kind.ToString(),
            m.Body,
            m.TemplateName,
            m.HasMedia,
            m.MediaMimeType,
            m.MediaFileName,
            m.Status.ToString(),
            m.OccurredAt,
            m.SentAt,
            m.DeliveredAt,
            m.ReadAt,
            m.FailedAt,
            m.ErrorCode,
            m.ErrorDetail,
            MediaPurged: m.MediaPurgedAt is not null)).ToList();

        return new WhatsAppMessagePageDto(items, hasMore ? page[^1].OccurredAt : null);
    }
}

// ── Read marker ──────────────────────────────────────────────────────────────────────────────

internal sealed record MarkWhatsAppReadCommand(Guid LeadId, bool HasWhatsAppPermission) : IRequest<Result>;

/// <summary>The agent opened the thread: its unread counter goes back to zero.</summary>
internal sealed class MarkWhatsAppReadHandler(LeadsDbContext db, WhatsAppLeadAccess access)
    : IRequestHandler<MarkWhatsAppReadCommand, Result>
{
    public async Task<Result> Handle(MarkWhatsAppReadCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.LeadId, cmd.HasWhatsAppPermission, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);

        var unread = await db.WhatsAppConversations
            .AsTracking()
            .Where(c => c.LeadId == cmd.LeadId && c.UnreadCount > 0)
            .ToListAsync(ct);

        foreach (var conversation in unread) conversation.MarkRead();

        if (unread.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A message arrived between the read and the write: its counter wins, and the
                // agent's next open clears it.
            }
        }

        return Result.Ok();
    }
}

// ── Media ────────────────────────────────────────────────────────────────────────────────────

internal sealed record GetWhatsAppMediaQuery(Guid LeadId, Guid MessageId, bool HasWhatsAppPermission)
    : ILeadReadRequest, IRequest<Result<MessagingMediaContent>>;

/// <summary>
/// A media of the lead's thread (WA-FE-05): our envelope-encrypted copy when one was kept
/// (WA-BE-20), the provider otherwise — see <see cref="WhatsAppMediaReader"/>.
/// </summary>
internal sealed class GetWhatsAppMediaHandler(
    LeadsDbContext db,
    WhatsAppLeadAccess access,
    ICurrentUser currentUser,
    WhatsAppMediaReader reader)
    : IRequestHandler<GetWhatsAppMediaQuery, Result<MessagingMediaContent>>
{
    public async Task<Result<MessagingMediaContent>> Handle(GetWhatsAppMediaQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.LeadId, query.HasWhatsAppPermission, ct);
        if (loaded.IsFailure) return Result.Fail<MessagingMediaContent>(loaded.Error!);

        var found = await (
                from m in db.WhatsAppMessages
                join c in db.WhatsAppConversations on m.ConversationId equals c.Id
                where m.Id == query.MessageId && c.LeadId == query.LeadId
                select new { m.MediaId, m.MediaStorageRef, m.MediaMimeType, m.MediaFileName, m.MediaPurgedAt, c.ConnectionId })
            .FirstOrDefaultAsync(ct);

        if (found is null) return Result.Fail<MessagingMediaContent>(WhatsAppErrors.MessageNotFound);

        return await reader.ReadAsync(
            currentUser.TenantId, found.ConnectionId, found.MediaStorageRef, found.MediaId, found.MediaMimeType,
            found.MediaFileName, found.MediaPurgedAt is not null, ct);
    }
}

// ── Compose ──────────────────────────────────────────────────────────────────────────────────

internal sealed record ComposeWhatsAppQuery(Guid LeadId, bool HasWhatsAppPermission)
    : IRequest<Result<IReadOnlyList<WhatsAppComposeTemplateDto>>>;

/// <summary>
/// The approved templates of the number that writes to this lead, meant for its product, with
/// every mapped variable already filled from the lead (WA-FE-04).
/// </summary>
internal sealed class ComposeWhatsAppHandler(
    LeadsDbContext db,
    WhatsAppLeadAccess access,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    IIntegrationModule integration)
    : IRequestHandler<ComposeWhatsAppQuery, Result<IReadOnlyList<WhatsAppComposeTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<WhatsAppComposeTemplateDto>>> Handle(ComposeWhatsAppQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.LeadId, query.HasWhatsAppPermission, ct);
        if (loaded.IsFailure) return Result.Fail<IReadOnlyList<WhatsAppComposeTemplateDto>>(loaded.Error!);

        var lead = loaded.Value!;
        var channel = await integration.Messaging.ResolveChannelAsync(currentUser.TenantId, lead.AgencyId, ct);

        if (channel is null) return Result.Ok<IReadOnlyList<WhatsAppComposeTemplateDto>>([]);

        var templates = (await db.WhatsAppTemplates
                .Where(t => t.ConnectionId == channel.ConnectionId
                         && t.IsAvailable
                         && t.Status == WhatsAppTemplateStatus.Approved)
                .OrderBy(t => t.Name)
                .ToListAsync(ct));

        // The product the lead ASKED for — the lead stores no line (product owner, 2026-10-24).
        var leadLine = await ProductLines.ProductLineLookup.ForLeadAsync(admin, lead, ct);
        templates = templates.Where(t => t.AppliesTo(leadLine)).ToList();

        var agentName = (await admin.GetUserNamesAsync(currentUser.TenantId, [currentUser.Id], ct))
            .GetValueOrDefault(currentUser.Id);

        var result = templates.Select(t => new WhatsAppComposeTemplateDto(
                t.Id, t.Name, t.Language, t.Category, t.HeaderText, t.BodyText, t.FooterText, t.Buttons,
                [.. Enumerable.Range(1, t.VariableCount).Select(i =>
                {
                    var mapping = t.VariableMappings.FirstOrDefault(m => m.Index == i);
                    return new WhatsAppComposeVariableDto(
                        i,
                        mapping?.Field.ToString(),
                        mapping is null ? string.Empty : ValueOf(mapping.Field, lead, agentName));
                })]))
            .ToList();

        return Result.Ok<IReadOnlyList<WhatsAppComposeTemplateDto>>(result);
    }

    /// <summary>The value of one mapped field. Empty when the lead does not carry it: the agent fills it.</summary>
    internal static string ValueOf(WhatsAppTemplateField field, Lead lead, string? agentName) => field switch
    {
        WhatsAppTemplateField.LeadFirstName => lead.FirstName ?? FirstWord(lead.FullName),
        WhatsAppTemplateField.LeadLastName => lead.LastName ?? string.Empty,
        WhatsAppTemplateField.LeadFullName => lead.FullName,
        // "Unknown" is the literal for « no product named », never something to write to a customer.
        WhatsAppTemplateField.InterestedProduct => lead.InterestedProduct is { Length: > 0 } p
                                                   && !string.Equals(p, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? p
            : string.Empty,
        WhatsAppTemplateField.AgentFirstName => FirstWord(agentName ?? string.Empty),
        WhatsAppTemplateField.AgentFullName => agentName ?? string.Empty,
        _ => string.Empty,
    };

    private static string FirstWord(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
}
