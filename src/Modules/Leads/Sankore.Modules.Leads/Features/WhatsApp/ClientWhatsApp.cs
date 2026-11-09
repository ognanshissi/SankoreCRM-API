namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Text.Json;
using System.Transactions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// WhatsApp with a CLIENT (2026-10-31, complaints lot 1): an advisor writes to the client — a
/// template to open the exchange, then free text inside the 24-hour window — usually about one of
/// their complaints, which every message sent from it names (<see cref="WhatsAppMessage.ComplaintId"/>).
/// Before, only a lead could be written to; a client's conversation was read-only.
///
/// <para>
/// <b>No consent is asked</b> (product owner): following up a complaint the client filed is a
/// service relationship, not prospecting — the workflow notifications already write to clients on
/// that footing. What is respected: the client's processing restriction, an anonymised client (no
/// number left), and a STOP from that number (<see cref="WhatsAppConversation.OptedOutAt"/>).
/// </para>
///
/// <para>
/// <b>Who:</b> anybody who may act on the client (M01's agency perimeter,
/// <c>ICustomersModule.CanActorAccessClientAsync</c>) and holds the WhatsApp permission — the
/// complaint's handler is its client's advisor or agency. The number is read on the server through
/// <c>ResolvePhoneForCallAsync</c>, like a click-to-call: never shown, never counted as a reveal.
/// </para>
/// </summary>
internal static class ClientWhatsAppErrors
{
    public const string ClientNotFound = "CLIENT_NOT_FOUND";
    public const string OptedOut = "WHATSAPP_CLIENT_OPTED_OUT";
    public const string NoConversation = "WHATSAPP_CONVERSATION_NOT_FOUND";
    public const string ComplaintNotFound = "COMPLAINT_NOT_FOUND";

    /// <summary>
    /// The client's number already has its conversation on a lead or on another client (a family
    /// phone): one conversation per number, and it is not this advisor's to read or write in.
    /// </summary>
    public const string NumberElsewhere = "WHATSAPP_NUMBER_IN_ANOTHER_RECORD";
}

/// <param name="Blockers">Why a template cannot be sent now; empty when it can.</param>
/// <param name="HeldByMe">Whether the caller holds the conversation — the condition of a free reply.</param>
public sealed record ClientWhatsAppDto(
    Guid? ConversationId,
    string? Status,
    Guid? OwnerUserId,
    bool HeldByMe,
    DateTimeOffset? WindowExpiresAt,
    bool WindowOpen,
    bool OptedOut,
    string? MaskedPhone,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<WhatsAppComposeTemplateDto> Templates);

public sealed record SendClientWhatsAppTemplateRequest(Guid TemplateId, IReadOnlyList<string>? Variables, Guid? ComplaintId);

public sealed record SendClientWhatsAppTextRequest(string Text, Guid? ComplaintId);

/// <summary>Everything a send to a client needs, read once: the client, the number, the channel, the conversation.</summary>
/// <param name="Conversation">
/// This client's conversation, or the number's conversation filed on nobody — never one of a lead
/// or of another client. Read and reply also require it to be filed on this client
/// (<see cref="Mine"/>); a template files a conversation on nobody on them.
/// </param>
/// <param name="NumberElsewhere">The number's conversation belongs to a lead or another client.</param>
internal sealed record ClientWhatsAppState(
    ClientSummary Client,
    string? Phone,
    string? PhoneIndex,
    MessagingChannelInfo? Channel,
    WhatsAppConversation? Conversation,
    bool Restricted,
    bool NumberElsewhere,
    bool LastTemplateFailed = false)
{
    /// <summary>The conversation, when it is filed on this client — what may be read and answered.</summary>
    public WhatsAppConversation? Mine => Conversation?.ClientId == Client.Id ? Conversation : null;
}

internal sealed class ClientWhatsAppContext(
    LeadsDbContext db,
    ICurrentUser me,
    ICustomersModule customers,
    IAdministrationModule admin,
    IIntegrationModule integration,
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer blindIndexer)
{
    public async Task<Result<ClientWhatsAppState>> LoadAsync(Guid clientId, CancellationToken ct)
    {
        ClientSummary? client;
        bool allowed, restricted;
        Result<string>? phone = null;
        string? callingCode;
        MessagingChannelInfo? channel;

        // Other modules' stores: suppressed, or the second connection promotes the ambient
        // transaction to a distributed one Npgsql refuses.
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            client = await customers.GetClientSummaryAsync(me.TenantId, clientId, ct);
            allowed = client is not null && await customers.CanActorAccessClientAsync(me.TenantId, clientId, me.Id, ct);
            restricted = allowed && await customers.IsProcessingRestrictedAsync(me.TenantId, clientId, ct);
            if (allowed && !restricted)
                phone = await customers.ResolvePhoneForCallAsync(me.TenantId, clientId, PhoneSlot.Primary, me.Id, ct);
            callingCode = (await admin.GetSmsConfigAsync(me.TenantId, ct))?.DefaultCallingCode;
            channel = client is null ? null : await integration.Messaging.ResolveChannelAsync(me.TenantId, client.AgencyId, ct);
            suppressed.Complete();
        }

        // Folded into « not found », never a 403: that would confirm another branch's client exists.
        if (client is null || !allowed) return Result.Fail<ClientWhatsAppState>(ClientWhatsAppErrors.ClientNotFound);

        var e164 = phone is { IsSuccess: true } ? WhatsAppNumbers.ToE164(phone.Value, callingCode) : null;
        var index = e164 is null ? null : blindIndexer.Compute(BlindIndexPurpose.Phone, e164);

        // The conversation of THIS number on the institution's number — only when it is this
        // client's or filed on nobody. One of a lead or of another client (a family phone) is not
        // this advisor's: its messages are that record's, so it is neither read nor written in.
        var byNumber = channel is not null && index is not null
            ? await db.WhatsAppConversations.AsTracking()
                .FirstOrDefaultAsync(c => c.ConnectionId == channel.ConnectionId && c.ContactPhoneBlindIndex == index, ct)
            : null;
        var usable = byNumber is { LeadId: null, AnonymizedAt: null } && (byNumber.ClientId is null || byNumber.ClientId == clientId);
        var conversation = usable ? byNumber : null;

        // Else the client's latest — their number may have changed since they last wrote.
        conversation ??= await db.WhatsAppConversations.AsTracking()
            .Where(c => c.ClientId == clientId && c.AnonymizedAt == null)
            .OrderByDescending(c => c.LastMessageAt)
            .FirstOrDefaultAsync(ct);

        return Result.Ok(new ClientWhatsAppState(
            client, e164, index, channel, conversation, restricted, NumberElsewhere: byNumber is not null && !usable,
            LastTemplateFailed: await WhatsAppTemplateCooldown.LastTemplateFailedAsync(db, conversation, ct)));
    }

    /// <summary>Why a template cannot go now, in the order the advisor can act on them.</summary>
    public static IReadOnlyList<string> Blockers(ClientWhatsAppState s, bool mayForce, DateTimeOffset now)
    {
        var blockers = new List<string>();
        if (s.Restricted) blockers.Add(WhatsAppErrors.ProcessingRestricted);
        if (s.Phone is null && !s.Restricted) blockers.Add(WhatsAppErrors.PhoneInvalid);
        if (s.Channel is null) blockers.Add(WhatsAppErrors.ChannelInactive);
        if (s.NumberElsewhere) blockers.Add(ClientWhatsAppErrors.NumberElsewhere);
        if (s.Conversation?.OptedOutAt is not null) blockers.Add(ClientWhatsAppErrors.OptedOut);
        if (!mayForce && !s.LastTemplateFailed && s.Conversation?.NextTemplateAllowedAt(now) is not null)
            blockers.Add(WhatsAppErrors.TemplateCooldown);
        return blockers;
    }

    /// <summary>The complaint named by a send, when it is this client's.</summary>
    public async Task<Result<Guid?>> ComplaintAsync(Guid clientId, Guid? complaintId, CancellationToken ct)
    {
        if (complaintId is not { } id) return Result.Ok<Guid?>(null);

        ClientComplaintReference? complaint;
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            complaint = await customers.GetComplaintReferenceAsync(me.TenantId, clientId, id, ct);
            suppressed.Complete();
        }

        return complaint is null
            ? Result.Fail<Guid?>(ClientWhatsAppErrors.ComplaintNotFound)
            : Result.Ok<Guid?>(complaint.Id);
    }
}

// ── The panel ────────────────────────────────────────────────────────────────────────────────

/// <param name="WithFile">The templates sent with a file — a project's (lot 3); else those sent as words alone.</param>
internal sealed record GetClientWhatsAppQuery(Guid ClientId, bool MayForce, bool WithFile = false) : IRequest<Result<ClientWhatsAppDto>>;

internal sealed class GetClientWhatsAppHandler(
    LeadsDbContext db, ClientWhatsAppContext context, ICurrentUser me, IAdministrationModule admin, TimeProvider clock)
    : IRequestHandler<GetClientWhatsAppQuery, Result<ClientWhatsAppDto>>
{
    public async Task<Result<ClientWhatsAppDto>> Handle(GetClientWhatsAppQuery query, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(query.ClientId, ct);
        if (loaded.IsFailure) return Result.Fail<ClientWhatsAppDto>(loaded.Error!);

        var s = loaded.Value!;
        var now = clock.GetUtcNow();
        var c = s.Mine;

        // Templates for every product only: a client's complaint is about no product a template could name.
        var templates = s.Channel is null
            ? []
            : await db.WhatsAppTemplates
                .Where(t => t.ConnectionId == s.Channel.ConnectionId && t.IsAvailable && t.Status == WhatsAppTemplateStatus.Approved)
                .OrderBy(t => t.Name)
                .ToListAsync(ct);

        var agentName = (await admin.GetUserNamesAsync(me.TenantId, [me.Id], ct)).GetValueOrDefault(me.Id);

        var compose = templates.Where(t => t.AppliesTo(null) && (query.WithFile ? t.IsSendableWithFile : t.IsSendableAsText)).Select(t => new WhatsAppComposeTemplateDto(
                t.Id, t.Name, t.Language, t.Category, t.HeaderText, t.BodyText, t.FooterText, t.Buttons,
                [.. Enumerable.Range(1, t.VariableCount).Select(i =>
                {
                    var mapping = t.VariableMappings.FirstOrDefault(m => m.Index == i);
                    return new WhatsAppComposeVariableDto(i, mapping?.Field.ToString(),
                        mapping is null ? string.Empty : ValueOf(mapping.Field, s.Client, agentName));
                })], t.FileHeader))
            .ToList();

        return Result.Ok(new ClientWhatsAppDto(
            c?.Id,
            c?.Status.ToString(),
            c?.OwnerUserId,
            c is { Status: WhatsAppConversationStatus.Human } && c.OwnerUserId == me.Id,
            c?.WindowExpiresAt,
            c?.IsWindowOpen(now) ?? false,
            c?.IsOptedOut ?? false,
            s.Phone is null ? null : WhatsAppNumbers.Mask(s.Phone),
            ClientWhatsAppContext.Blockers(s, query.MayForce, now),
            compose));
    }

    /// <summary>A lead field mapped on a template, read off the client: their name; the rest is the advisor's to type.</summary>
    private static string ValueOf(WhatsAppTemplateField field, ClientSummary client, string? agentName) => field switch
    {
        WhatsAppTemplateField.LeadFirstName => client.ClientType == "Individual" ? FirstWord(client.DisplayName) : client.DisplayName,
        WhatsAppTemplateField.LeadFullName => client.DisplayName,
        WhatsAppTemplateField.AgentFirstName => FirstWord(agentName ?? string.Empty),
        WhatsAppTemplateField.AgentFullName => agentName ?? string.Empty,
        _ => string.Empty,
    };

    private static string FirstWord(string value) => value.Trim().Split(' ', 2)[0];
}

internal sealed record ListClientWhatsAppMessagesQuery(Guid ClientId, Guid? ComplaintId, DateTimeOffset? Before, int Limit)
    : IRequest<Result<WhatsAppMessagePageDto>>;

/// <summary>The client's thread, newest first — or only what was written about one complaint.</summary>
internal sealed class ListClientWhatsAppMessagesHandler(
    LeadsDbContext db, ClientWhatsAppContext context, ICurrentUser me, IAdministrationModule admin)
    : IRequestHandler<ListClientWhatsAppMessagesQuery, Result<WhatsAppMessagePageDto>>
{
    public async Task<Result<WhatsAppMessagePageDto>> Handle(ListClientWhatsAppMessagesQuery query, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(query.ClientId, ct);
        if (loaded.IsFailure) return Result.Fail<WhatsAppMessagePageDto>(loaded.Error!);
        if (loaded.Value!.Mine is not { } conversation) return Result.Ok(new WhatsAppMessagePageDto([], null));

        var page = await WhatsAppMessagePages.ReadAsync(
            db, admin, me.TenantId, [conversation.Id], query.Before, Math.Clamp(query.Limit, 1, 100), ct);

        return Result.Ok(query.ComplaintId is { } complaint
            ? page with { Items = page.Items.Where(m => m.ComplaintId == complaint).ToList() }
            : page);
    }
}

// ── Sending ──────────────────────────────────────────────────────────────────────────────────

/// <param name="File">The file of a template with a DOCUMENT or IMAGE header — set by a project's route only (lot 3).</param>
/// <param name="ProjectId">The project the file is from; the message is linked to it.</param>
internal sealed record SendClientWhatsAppTemplateCommand(
    Guid ClientId, Guid TemplateId, [property: SensitiveData] IReadOnlyList<string> Variables, Guid? ComplaintId, bool MayForce,
    [property: SensitiveData] TemplateFile? File = null, Guid? ProjectId = null, Guid? ProjectAttachmentId = null)
    : IRequest<Result<SendWhatsAppTemplateResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>Opens the exchange with a template, as <see cref="SendWhatsAppTemplateHandler"/> does for a lead.</summary>
internal sealed class SendClientWhatsAppTemplateHandler(
    LeadsDbContext db, ClientWhatsAppContext context, ICurrentUser me,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock,
    IServiceProvider? services = null)
    : IRequestHandler<SendClientWhatsAppTemplateCommand, Result<SendWhatsAppTemplateResult>>
{
    public async Task<Result<SendWhatsAppTemplateResult>> Handle(SendClientWhatsAppTemplateCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure) return Fail(loaded.Error!);
        var s = loaded.Value!;

        var blockers = ClientWhatsAppContext.Blockers(s, cmd.MayForce, now);
        if (blockers.Count > 0) return Fail(blockers[0]);

        var complaint = await context.ComplaintAsync(cmd.ClientId, cmd.ComplaintId, ct);
        if (complaint.IsFailure) return Fail(complaint.Error!);

        var template = await db.WhatsAppTemplates
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId && t.ConnectionId == s.Channel!.ConnectionId, ct);
        if (template is null) return Fail(WhatsAppErrors.TemplateNotFound);
        if (!template.IsSendable) return Fail(WhatsAppErrors.TemplateNotApproved);
        if (!template.AppliesTo(null)) return Fail(WhatsAppErrors.TemplateNotForProduct);
        if (WhatsAppTemplateFiles.Problem(template, cmd.File, out var media) is { } fileProblem) return Fail(fileProblem);

        var values = (cmd.Variables ?? []).Select(v => v?.Trim() ?? string.Empty).ToList();
        if (values.Count != template.VariableCount
            || values.Any(v => v.Length == 0 || v.Length > SendWhatsAppTemplateHandler.MaxVariableLength || v.Contains('\n') || v.Contains('\t')))
            return Fail(WhatsAppErrors.VariablesInvalid);

        var conversation = s.Conversation;
        if (conversation is null || conversation.ConnectionId != s.Channel!.ConnectionId || conversation.ContactPhoneBlindIndex != s.PhoneIndex)
        {
            // The number on file now, on the agency's institution number — a fresh conversation
            // when the client's latest one was on another number.
            conversation = WhatsAppConversation.Open(
                me.TenantId, s.Channel!.ConnectionId, s.Phone!, s.PhoneIndex!, leadId: null, clientId: cmd.ClientId,
                contactName: s.Client.DisplayName, ambiguousMatch: false, now);
            db.WhatsAppConversations.Add(conversation);
        }
        else
        {
            conversation.AttachToClient(cmd.ClientId);
        }

        var message = WhatsAppMessage.OutboundTemplate(
            me.TenantId, conversation.Id, me.Id, template.Id, template.Name, template.Language,
            template.Render(values), JsonSerializer.Serialize(values), now);
        if (complaint.Value is { } complaintId) message.LinkToComplaint(complaintId);
        if (cmd.ProjectId is { } projectId) message.LinkToProject(projectId, cmd.ProjectAttachmentId);

        var kept = await WhatsAppTemplateFiles.KeepAsync(services, message, cmd.File, media, me.TenantId, now, ct);
        if (kept.IsFailure) return Fail(kept.Error!);

        db.WhatsAppMessages.Add(message);
        conversation.RecordTemplateSent(now);

        await publisher.PublishAsync(new WhatsAppMessageQueuedEvent(me.TenantId, message.Id), ct);
        await WhatsAppTemplateFiles.SaveAsync(db, services, message, ct);

        return Result.Ok(new SendWhatsAppTemplateResult(conversation.Id, message.Id));
    }

    private static Result<SendWhatsAppTemplateResult> Fail(string code) => Result.Fail<SendWhatsAppTemplateResult>(code);
}

internal sealed record TakeOverClientWhatsAppCommand(Guid ClientId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>The advisor takes the client's conversation from the bot or the queue, to answer it.</summary>
internal sealed class TakeOverClientWhatsAppHandler(
    LeadsDbContext db, ClientWhatsAppContext context, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<TakeOverClientWhatsAppCommand, Result>
{
    public async Task<Result> Handle(TakeOverClientWhatsAppCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);
        if (loaded.Value!.Mine is not { } conversation) return Result.Fail(ClientWhatsAppErrors.NoConversation);

        var now = clock.GetUtcNow();
        var taken = conversation.TakeOver(me.Id, now);
        if (taken.IsFailure) return taken;

        await WhatsAppHandovers.EndAsync(db, conversation.Id, WhatsAppHandoverEnd.TakenOver, me.Id, now, ct);
        conversation.MarkRead();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record SendClientWhatsAppTextCommand(Guid ClientId, [property: SensitiveData] string Text, Guid? ComplaintId)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// A free reply: by the advisor who holds the conversation, inside the client's 24-hour window,
/// never in answer to a STOP, never to a client whose processing is restricted.
/// </summary>
internal sealed class SendClientWhatsAppTextHandler(
    LeadsDbContext db, ClientWhatsAppContext context, ICurrentUser me,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock)
    : IRequestHandler<SendClientWhatsAppTextCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SendClientWhatsAppTextCommand cmd, CancellationToken ct)
    {
        var text = cmd.Text?.Trim() ?? string.Empty;
        if (text.Length is 0 or > WhatsAppMessage.BodyMaxLength) return Result.Fail<Guid>(WhatsAppConversationErrors.TextInvalid);

        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure) return Result.Fail<Guid>(loaded.Error!);
        var s = loaded.Value!;

        var now = clock.GetUtcNow();
        if (ClientReplyRules.Check(s, me.Id, now) is { } refusal) return Result.Fail<Guid>(refusal);
        var conversation = s.Mine!;

        var complaint = await context.ComplaintAsync(cmd.ClientId, cmd.ComplaintId, ct);
        if (complaint.IsFailure) return Result.Fail<Guid>(complaint.Error!);

        var message = WhatsAppMessage.OutboundText(me.TenantId, conversation.Id, WhatsAppAuthor.Agent, me.Id, text, now);
        if (complaint.Value is { } complaintId) message.LinkToComplaint(complaintId);
        db.WhatsAppMessages.Add(message);
        conversation.RecordOutbound(now);

        await publisher.PublishAsync(new WhatsAppMessageQueuedEvent(me.TenantId, message.Id), ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(message.Id);
    }
}

/// <summary>
/// The conditions of a free message to a client, shared by text, file and a project's documents:
/// a conversation filed on them, held by the caller, inside the 24-hour window, no STOP, no
/// processing restriction. In the order the advisor can act on them.
/// </summary>
internal static class ClientReplyRules
{
    public static string? Check(ClientWhatsAppState s, Guid me, DateTimeOffset now)
    {
        if (s.Restricted) return WhatsAppErrors.ProcessingRestricted;
        if (s.Mine is not { } conversation) return ClientWhatsAppErrors.NoConversation;
        if (conversation.Status != WhatsAppConversationStatus.Human || conversation.OwnerUserId != me)
            return WhatsAppConversationErrors.NotHolder;
        if (!conversation.IsWindowOpen(now)) return WhatsAppConversationErrors.WindowClosed;
        if (conversation.IsOptedOut) return ClientWhatsAppErrors.OptedOut;
        return null;
    }
}

/// <param name="Content">The file's bytes. Marked sensitive: never written to the audit trail.</param>
internal sealed record SendClientWhatsAppMediaCommand(
    Guid ClientId,
    [property: SensitiveData] byte[] Content,
    string ContentType,
    [property: SensitiveData] string FileName,
    [property: SensitiveData] string? Caption,
    Guid? ComplaintId)
    : IRequest<Result<SendWhatsAppMediaResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// An image or a PDF to the client (project documents lot 2, 2026-11-08): the lead's media reply,
/// for a client — same rules as a free text to them, optionally about one of their complaints.
/// </summary>
internal sealed class SendClientWhatsAppMediaHandler(
    ClientWhatsAppContext context, ICurrentUser me, WhatsAppMediaSender sender, TimeProvider clock)
    : IRequestHandler<SendClientWhatsAppMediaCommand, Result<SendWhatsAppMediaResult>>
{
    public async Task<Result<SendWhatsAppMediaResult>> Handle(SendClientWhatsAppMediaCommand cmd, CancellationToken ct)
    {
        var media = WhatsAppMediaSender.Check(cmd.Content, cmd.ContentType);
        if (media.IsFailure) return Result.Fail<SendWhatsAppMediaResult>(media.Error!);

        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure) return Result.Fail<SendWhatsAppMediaResult>(loaded.Error!);
        var s = loaded.Value!;

        if (ClientReplyRules.Check(s, me.Id, clock.GetUtcNow()) is { } refusal) return Result.Fail<SendWhatsAppMediaResult>(refusal);

        var complaint = await context.ComplaintAsync(cmd.ClientId, cmd.ComplaintId, ct);
        if (complaint.IsFailure) return Result.Fail<SendWhatsAppMediaResult>(complaint.Error!);

        return Result.Ok(await sender.SendAsync(s.Mine!, media.Value!, cmd.Content, cmd.FileName, cmd.Caption,
            message => { if (complaint.Value is { } id) message.LinkToComplaint(id); }, ct));
    }
}

// ── Routes ───────────────────────────────────────────────────────────────────────────────────

internal static class ClientWhatsAppEndpoints
{
    public static IEndpointRouteBuilder MapClientWhatsAppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("whatsapp/clients/{clientId:guid}",
                async (Guid clientId, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new GetClientWhatsAppQuery(clientId, MayForce(http)), ct)))
            .WithName("GetClientWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("The client's WhatsApp exchange: conversation, window, what blocks a send, the templates")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<ClientWhatsAppDto>()
            .WithTenantHeader();

        app.MapGet("whatsapp/clients/{clientId:guid}/messages",
                async (Guid clientId, Guid? complaintId, DateTimeOffset? before, int? limit, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ListClientWhatsAppMessagesQuery(clientId, complaintId, before, limit ?? 30), ct)))
            .WithName("ListClientWhatsAppMessages")
            .WithTags("WhatsApp")
            .WithSummary("The client's messages, newest first; `complaintId` keeps those written about one complaint")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<WhatsAppMessagePageDto>()
            .WithTenantHeader();

        app.MapPost("whatsapp/clients/{clientId:guid}/send-template",
                async (Guid clientId, SendClientWhatsAppTemplateRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SendClientWhatsAppTemplateCommand(
                        clientId, req.TemplateId, req.Variables ?? [], req.ComplaintId, MayForce(http)), ct)))
            .WithName("SendClientWhatsAppTemplate")
            .WithTags("WhatsApp")
            .WithSummary("Open the exchange with the client by a template, optionally about one of their complaints")
            .WithDescription(
                "No consent asked: following up the client's own complaint is a service relationship. Refused when the "
                + "client's processing is restricted, has no usable number, wrote STOP (WHATSAPP_CLIENT_OPTED_OUT), or a "
                + "template went unanswered in the last 24 h (unless WhatsApp.Send.Force).")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<SendWhatsAppTemplateResult>()
            .WithTenantHeader();

        app.MapPost("whatsapp/clients/{clientId:guid}/takeover",
                async (Guid clientId, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new TakeOverClientWhatsAppCommand(clientId), ct)))
            .WithName("TakeOverClientWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("Take the client's conversation, to answer it freely")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .WithTenantHeader();

        app.MapPost("whatsapp/clients/{clientId:guid}/messages",
                async (Guid clientId, SendClientWhatsAppTextRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SendClientWhatsAppTextCommand(clientId, req.Text, req.ComplaintId), ct)))
            .WithName("SendClientWhatsAppText")
            .WithTags("WhatsApp")
            .WithSummary("A free reply to the client, inside their 24-hour window, by the advisor who holds the conversation")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .Produces<Guid>()
            .WithTenantHeader();

        app.MapPost("whatsapp/clients/{clientId:guid}/media",
                async (Guid clientId, IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] string? caption,
                       [Microsoft.AspNetCore.Mvc.FromForm] Guid? complaintId, ISender sender, CancellationToken ct) =>
                {
                    if (file is null || file.Length is 0 or > WhatsAppMediaSender.MaxDocumentBytes)
                        return Problem(WhatsAppConversationErrors.MediaInvalid);

                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, ct);
                    return Map(await sender.Send(new SendClientWhatsAppMediaCommand(
                        clientId, buffer.ToArray(), file.ContentType, file.FileName, caption, complaintId), ct));
                })
            .WithName("SendClientWhatsAppMedia")
            .WithTags("WhatsApp")
            .WithSummary("Sends the client an image (JPEG, PNG, 5 MB) or a PDF (10 MB) — holder only, inside their window")
            .WithDescription(
                "multipart/form-data: `file`, optional `caption` and `complaintId`. Same refusals as a free text, plus "
                + "WHATSAPP_MEDIA_TYPE_NOT_ALLOWED (not an image nor a PDF) and WHATSAPP_MEDIA_INVALID (empty, too large, "
                + "bytes that are not what the type says). A provider refusal answers 200 with status Failed.")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<SendWhatsAppMediaResult>()
            .WithTenantHeader();

        return app;
    }

    private static bool MayForce(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanForceWhatsAppSend.Code);

    private static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        ClientWhatsAppErrors.ClientNotFound or ClientWhatsAppErrors.NoConversation or ClientWhatsAppErrors.ComplaintNotFound
            or WhatsAppErrors.TemplateNotFound => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status404NotFound),
        _ => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
