namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// One conversation, by its own id (WA-BE-19) — the way to open a conversation that is filed on
/// no lead: a client's, or a number nobody recognised. A lead's conversation opens here too; its
/// actions stay on the lead's tab, where the lead's ownership rule applies.
/// </summary>
/// <param name="Name">The lead's or client's name, else the WhatsApp profile name.</param>
/// <param name="MaskedPhone">The customer's number, masked — enough to recognise, not to dial.</param>
/// <param name="Route">The lead's WhatsApp tab when it is filed on one.</param>
/// <param name="CanAttach">Filed on no lead and not anonymised: an agent may file it on one.</param>
/// <param name="CollectingProspect">The bot is collecting an unknown number's lead, which does not exist yet.</param>
/// <param name="PendingFields">What the bot collected for that lead so far — empty once the lead exists.</param>
public sealed record WhatsAppConversationDetailDto(
    Guid Id,
    Guid? LeadId,
    Guid? ClientId,
    string Name,
    string? MaskedPhone,
    string Status,
    Guid? OwnerUserId,
    string? OwnerName,
    Guid? TargetAgentId,
    string? TargetAgentName,
    DateTimeOffset? WindowExpiresAt,
    DateTimeOffset LastMessageAt,
    int UnreadCount,
    bool AmbiguousMatch,
    string? EscalationReason,
    string? EscalationReasonLabel,
    string? EscalationSummary,
    bool Anonymized,
    string? Route,
    bool CanAttach,
    bool CollectingProspect = false,
    IReadOnlyList<WhatsAppBotCollectedFieldDto>? PendingFields = null);

public sealed record AttachWhatsAppConversationRequest(Guid LeadId);

// ── Detail ───────────────────────────────────────────────────────────────────────────────────

internal sealed record GetWhatsAppConversationQuery(Guid ConversationId, bool HasWhatsAppPermission, bool MaySupervise)
    : IRequest<Result<WhatsAppConversationDetailDto>>;

/// <summary>
/// Inside the caller's perimeter only — the same <see cref="WhatsAppPerimeter"/> as the inbox, so
/// what the inbox lists opens and nothing else does. Outside it, « not found », never « forbidden »:
/// the existence of another branch's conversation is not the caller's to learn. A supervisor
/// opening a conversation they are not involved in is audited (WA-BE-20).
/// </summary>
internal sealed class GetWhatsAppConversationHandler(
    WhatsAppPerimeter perimeter,
    ICurrentUser me,
    IAdministrationModule admin,
    ICustomersModule customers,
    WhatsAppAudit audit,
    Bot.WhatsAppBotFieldValues fieldValues)
    : IRequestHandler<GetWhatsAppConversationQuery, Result<WhatsAppConversationDetailDto>>
{
    public async Task<Result<WhatsAppConversationDetailDto>> Handle(GetWhatsAppConversationQuery query, CancellationToken ct)
    {
        if (!query.HasWhatsAppPermission && !query.MaySupervise)
            return Result.Fail<WhatsAppConversationDetailDto>(LeadAccessGuard.Forbidden);

        var row = await perimeter.FindAsync(query.ConversationId, query.MaySupervise, ct);
        if (row is null) return Result.Fail<WhatsAppConversationDetailDto>(WhatsAppConversationErrors.ConversationNotFound);

        var c = row.Conversation;

        if (!WhatsAppPerimeter.IsInvolved(me.Id, c, row.Lead))
            await audit.ConsultedAsync(me.TenantId, me.Id, c.Id, c.LeadId, ct);

        var people = new[] { c.OwnerUserId, c.TargetAgentId }.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(me.TenantId, people, ct);

        string? clientName = null;

        if (row.Lead is null && c.ClientId is { } clientId)
        {
            try
            {
                clientName = (await customers.GetClientSummaryAsync(me.TenantId, clientId, ct))?.DisplayName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // M01 down: the profile name stands in.
                _ = ex;
            }
        }

        var anonymized = c.AnonymizedAt is not null;

        // A prospect's pending values name a product and custom fields: the catalogue and the lead
        // form are read only when there is something pending to show.
        var pending = c.PendingLeadFields();
        var fields = pending.Count > 0 ? await fieldValues.ContextAsync(c.TenantId, ct) : null;

        return Result.Ok(new WhatsAppConversationDetailDto(
            c.Id,
            c.LeadId,
            c.ClientId,
            row.Lead?.FullName ?? clientName ?? c.ContactName ?? "Contact WhatsApp",
            anonymized ? null : WhatsAppNumbers.Mask(c.ContactPhone),
            c.Status.ToString(),
            c.OwnerUserId,
            c.OwnerUserId is { } o ? names.GetValueOrDefault(o) : null,
            c.TargetAgentId,
            c.TargetAgentId is { } t ? names.GetValueOrDefault(t) : null,
            c.WindowExpiresAt,
            c.LastMessageAt,
            c.UnreadCount,
            c.AmbiguousMatch,
            c.EscalationReason,
            c.EscalationReason is null ? null : Bot.WhatsAppBotReasons.Label(c.EscalationReason),
            c.EscalationSummary,
            anonymized,
            c.LeadId is { } lead ? $"/leads/{lead}/suivi/whatsapp" : null,
            CanAttach: c.LeadId is null && !anonymized,
            CollectingProspect: c is { CollectingProspect: true, LeadId: null },
            PendingFields: pending
                .Select(p => new WhatsAppBotCollectedFieldDto(p.Key, fields?.LabelOf(p.Key) ?? WhatsAppBotFields.LabelOf(p.Key),
                    Bot.WhatsAppBotFieldValues.Display(null, pending, p.Key, fields)))
                .ToList()));
    }
}

// ── Messages ─────────────────────────────────────────────────────────────────────────────────

internal sealed record ListWhatsAppConversationMessagesQuery(
    Guid ConversationId, bool HasWhatsAppPermission, bool MaySupervise, DateTimeOffset? Before, int Limit)
    : IRequest<Result<WhatsAppMessagePageDto>>;

internal sealed class ListWhatsAppConversationMessagesHandler(
    LeadsDbContext db, WhatsAppPerimeter perimeter, ICurrentUser me, IAdministrationModule admin)
    : IRequestHandler<ListWhatsAppConversationMessagesQuery, Result<WhatsAppMessagePageDto>>
{
    public async Task<Result<WhatsAppMessagePageDto>> Handle(ListWhatsAppConversationMessagesQuery query, CancellationToken ct)
    {
        if (!query.HasWhatsAppPermission && !query.MaySupervise)
            return Result.Fail<WhatsAppMessagePageDto>(LeadAccessGuard.Forbidden);

        var row = await perimeter.FindAsync(query.ConversationId, query.MaySupervise, ct);
        if (row is null) return Result.Fail<WhatsAppMessagePageDto>(WhatsAppConversationErrors.ConversationNotFound);

        return Result.Ok(await WhatsAppMessagePages.ReadAsync(
            db, admin, me.TenantId, [row.Conversation.Id], query.Before, Math.Clamp(query.Limit, 1, 100), ct));
    }
}

internal sealed record GetWhatsAppConversationMediaQuery(Guid ConversationId, Guid MessageId, bool HasWhatsAppPermission, bool MaySupervise)
    : IRequest<Result<MessagingMediaContent>>;

internal sealed class GetWhatsAppConversationMediaHandler(
    LeadsDbContext db, WhatsAppPerimeter perimeter, ICurrentUser me, WhatsAppMediaReader reader)
    : IRequestHandler<GetWhatsAppConversationMediaQuery, Result<MessagingMediaContent>>
{
    public async Task<Result<MessagingMediaContent>> Handle(GetWhatsAppConversationMediaQuery query, CancellationToken ct)
    {
        if (!query.HasWhatsAppPermission && !query.MaySupervise)
            return Result.Fail<MessagingMediaContent>(LeadAccessGuard.Forbidden);

        var row = await perimeter.FindAsync(query.ConversationId, query.MaySupervise, ct);
        if (row is null) return Result.Fail<MessagingMediaContent>(WhatsAppConversationErrors.ConversationNotFound);

        var message = await db.WhatsAppMessages
            .FirstOrDefaultAsync(m => m.Id == query.MessageId && m.ConversationId == query.ConversationId, ct);

        if (message is null) return Result.Fail<MessagingMediaContent>(WhatsAppErrors.MessageNotFound);

        return await reader.ReadAsync(
            me.TenantId, row.Conversation.ConnectionId, message.MediaStorageRef, message.MediaId, message.MediaMimeType,
            message.MediaFileName, message.MediaPurgedAt is not null, ct);
    }
}

// ── Attach to a lead ─────────────────────────────────────────────────────────────────────────

internal sealed record AttachWhatsAppConversationCommand(Guid ConversationId, Guid LeadId, bool HasWhatsAppPermission, bool MaySupervise)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppConversation";
    public string? ResourceId => ConversationId.ToString();
}

/// <summary>
/// Files a conversation that is on no lead — a number nobody recognised, a client's — on a lead
/// (WA-BE-09's « non rattachés » queue, WA-BE-19). From then on it opens on the lead's tab and its
/// actions follow the lead's ownership rule.
///
/// <para>
/// The caller must see the conversation (the perimeter) AND be allowed to work the lead
/// (<see cref="LeadAccessGuard"/>): filing a stranger's messages on a lead one does not work would
/// put them in front of whoever does. A conversation already on a lead is never moved here — a
/// misfiled conversation is a supervisor's correction, not an agent's attach. Audited as a command.
/// </para>
/// </summary>
internal sealed class AttachWhatsAppConversationHandler(
    LeadsDbContext db, WhatsAppPerimeter perimeter, LeadAccessGuard guard)
    : IRequestHandler<AttachWhatsAppConversationCommand, Result>
{
    public async Task<Result> Handle(AttachWhatsAppConversationCommand cmd, CancellationToken ct)
    {
        if (!cmd.HasWhatsAppPermission && !cmd.MaySupervise) return Result.Fail(LeadAccessGuard.Forbidden);

        var row = await perimeter.FindAsync(cmd.ConversationId, cmd.MaySupervise, ct);
        if (row is null) return Result.Fail(WhatsAppConversationErrors.ConversationNotFound);
        if (row.Conversation.LeadId is not null) return Result.Fail(WhatsAppErrors.AlreadyAttached);
        if (row.Conversation.AnonymizedAt is not null) return Result.Fail(WhatsAppConversationErrors.ConversationNotFound);

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);
        if (lead is null) return Result.Fail(WhatsAppErrors.LeadNotFound);

        var allowed = await guard.EnsureCanWorkAsync(lead, ct);
        if (allowed.IsFailure) return allowed;

        var conversation = await db.WhatsAppConversations.AsTracking().FirstAsync(c => c.Id == cmd.ConversationId, ct);
        conversation.AttachToLead(lead.Id);

        // The open handover, if any, belongs to the lead from now on too.
        if (await WhatsAppHandovers.FindOpenAsync(db, conversation.Id, ct) is { } open)
            open.AttachToLead(lead.Id);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
