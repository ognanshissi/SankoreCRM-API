namespace Sankore.Modules.Leads.Features.Opportunities.Sending;

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
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

// Project documents lot 2 (2026-11-08, product owner): a project's devis, proposal… sent by
// WhatsApp from its page, inside the 24-hour window. Outside it, lot 3 sends them in a template
// with a document header.

internal static class ProjectWhatsAppErrors
{
    /// <summary>The project has neither a lead nor a client to write to.</summary>
    public const string NoRecipient = "PROJECT_WHATSAPP_NO_RECIPIENT";

    /// <summary>No file named, or more than <see cref="ProjectWhatsAppTarget.MaxFiles"/>.</summary>
    public const string FilesInvalid = "PROJECT_WHATSAPP_FILES_INVALID";
}

/// <param name="Sendable">An image (JPEG, PNG ≤ 5 MB) or a PDF (≤ 10 MB): WhatsApp carries it.</param>
/// <param name="Reason">Why not: <c>WHATSAPP_MEDIA_TYPE_NOT_ALLOWED</c> (by e-mail only) or <c>WHATSAPP_MEDIA_INVALID</c> (too large).</param>
public sealed record ProjectWhatsAppFileDto(Guid AttachmentId, string FieldKey, string FileName, string ContentType, long SizeBytes, bool Sendable, string? Reason);

/// <param name="Recipient"><c>Lead</c> or <c>Client</c>: who the documents go to — the project's lead when it has one, else its client.</param>
/// <param name="Blockers">Why no file can be sent FREELY now (inside the window), first the one to act on; empty when it can.</param>
/// <param name="TemplateBlockers">
/// Why no template with a file can be sent now (project documents lot 3) — the record's own
/// template rules: consent for a lead, a STOP, the 24-hour cooldown… Empty when it can.
/// </param>
/// <param name="Templates">The approved templates with a DOCUMENT or IMAGE header for this recipient, variables filled.</param>
public sealed record ProjectWhatsAppDto(
    string? Recipient,
    Guid? RecipientId,
    Guid? ConversationId,
    string? ConversationStatus,
    bool HeldByMe,
    bool WindowOpen,
    DateTimeOffset? WindowExpiresAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<ProjectWhatsAppFileDto> Files,
    IReadOnlyList<string> TemplateBlockers,
    IReadOnlyList<WhatsAppComposeTemplateDto> Templates);

public sealed record SendProjectWhatsAppDocumentsRequest(IReadOnlyList<Guid> AttachmentIds, string? Caption);

/// <param name="AttachmentId">The project's file the template's header carries: a PDF for a DOCUMENT header, a JPEG or PNG for an IMAGE one.</param>
/// <param name="Variables">The values of <c>{{1}}</c>, <c>{{2}}</c>… in order.</param>
public sealed record SendProjectWhatsAppTemplateRequest(Guid TemplateId, Guid AttachmentId, IReadOnlyList<string>? Variables);

/// <param name="Status"><c>Sent</c>, or <c>Failed</c> with <paramref name="ErrorCode"/> — the provider refused that one.</param>
public sealed record ProjectWhatsAppSentDto(Guid AttachmentId, Guid MessageId, string Status, string? ErrorCode);

/// <summary>Who a project writes to on WhatsApp, in which conversation, and what stops it now.</summary>
internal sealed record ProjectWhatsAppState(
    Opportunity Project,
    string? Recipient,
    Guid? RecipientId,
    WhatsAppConversation? Conversation,
    IReadOnlyList<string> Blockers);

/// <summary>
/// The recipient of a project's documents and the rules of a free message to them — the SAME rules
/// as the lead's and the client's own routes, so a project page never sends what the record's
/// WhatsApp tab would refuse:
/// <list type="bullet">
/// <item>a project with a lead writes to the lead, in the conversation its tab presents, provided it
/// is filed on that lead; the caller must be allowed to work the lead (<c>LeadAccessGuard</c>);
/// holder, window, consent not withdrawn, the person's processing not restricted;</item>
/// <item>a project opened on a client writes to the client (<see cref="ClientWhatsAppContext"/>,
/// <see cref="ClientReplyRules"/>) — no consent asked, a service relationship.</item>
/// </list>
/// The project itself must be in the caller's perimeter (the page's <see cref="LeadVisibility"/>).
/// </summary>
internal sealed class ProjectWhatsAppTarget(
    LeadsDbContext db,
    ICurrentUser me,
    LeadVisibility visibility,
    WhatsAppLeadAccess leadAccess,
    IAdministrationModule admin,
    IIntegrationModule integration,
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer blindIndexer,
    IServiceProvider services,
    TimeProvider clock,
    ICustomersModule? customers = null)
{
    /// <summary>One message per file: a handful at a time, not a mailing.</summary>
    public const int MaxFiles = 5;

    /// <summary>The project, in the caller's perimeter, and who it writes to: its lead (not anonymised), else its client, else nobody.</summary>
    public async Task<Result<(Opportunity Project, string? Recipient, Guid? RecipientId)>> RecipientAsync(Guid projectId, CancellationToken ct)
    {
        var project = await visibility.Apply(db.Opportunities, db.Leads).AsNoTracking().FirstOrDefaultAsync(o => o.Id == projectId, ct);
        if (project is null) return Result.Fail<(Opportunity, string?, Guid?)>(OpportunityErrors.NotFound);

        if (project.LeadId is { } leadId && await db.Leads.AnyAsync(l => l.Id == leadId && !l.IsAnonymized, ct))
            return Result.Ok<(Opportunity, string?, Guid?)>((project, "Lead", leadId));
        if (project.CustomerEntityType == "Customer" && project.CustomerEntityId is { } clientId)
            return Result.Ok<(Opportunity, string?, Guid?)>((project, "Client", clientId));
        return Result.Ok<(Opportunity, string?, Guid?)>((project, null, null));
    }

    public async Task<Result<ProjectWhatsAppState>> LoadAsync(Guid projectId, CancellationToken ct)
    {
        var target = await RecipientAsync(projectId, ct);
        if (target.IsFailure) return Result.Fail<ProjectWhatsAppState>(target.Error!);
        var (project, recipient, recipientId) = target.Value;

        var now = clock.GetUtcNow();

        if (recipient == "Lead" && recipientId is { } leadId)
        {
            var lead = await leadAccess.LoadAsync(leadId, hasWhatsAppPermission: true, ct);
            if (lead.IsFailure) return Result.Ok(new ProjectWhatsAppState(project, "Lead", leadId, null, [lead.Error!]));

            var (_, _, presented) = await WhatsAppLeadConversations.PresentedAsync(
                db, admin, integration, blindIndexer, lead.Value!, tracking: true, ct);
            var conversation = presented?.LeadId == leadId ? presented : null;

            string? blocker;
            if (await Retention.ProcessingRestriction.IsRestrictedAsync(customers, me.TenantId, lead.Value!, ct))
                blocker = WhatsAppErrors.ProcessingRestricted;
            else if (conversation is null)
                blocker = WhatsAppConversationErrors.ConversationNotFound;
            else
                blocker = await ReplyRules.CheckAsync(db, lead.Value!, conversation, me.Id, now, ct);

            return Result.Ok(new ProjectWhatsAppState(project, "Lead", leadId, conversation, blocker is null ? [] : [blocker]));
        }

        if (recipient == "Client" && recipientId is { } clientId)
        {
            var client = await services.GetRequiredService<ClientWhatsAppContext>().LoadAsync(clientId, ct);
            if (client.IsFailure) return Result.Ok(new ProjectWhatsAppState(project, "Client", clientId, null, [client.Error!]));

            var blocker = ClientReplyRules.Check(client.Value!, me.Id, now);
            return Result.Ok(new ProjectWhatsAppState(project, "Client", clientId, client.Value!.Mine, blocker is null ? [] : [blocker]));
        }

        return Result.Ok(new ProjectWhatsAppState(project, null, null, null, [ProjectWhatsAppErrors.NoRecipient]));
    }

    /// <summary>The project's live files, each said sendable or not by WhatsApp — from the row, no bytes read.</summary>
    public async Task<IReadOnlyList<ProjectWhatsAppFileDto>> FilesAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.ProjectAttachments.AsNoTracking()
            .Where(a => a.OpportunityId == projectId && a.DeletedAt == null)
            .OrderBy(a => a.UploadedAt)
            .ToListAsync(ct);

        return rows.Select(a =>
        {
            var reason = ReasonNotSendable(a.ContentType, a.SizeBytes);
            return new ProjectWhatsAppFileDto(a.Id, a.FieldKey, a.FileName, a.ContentType, a.SizeBytes, reason is null, reason);
        }).ToList();
    }

    internal static string? ReasonNotSendable(string contentType, long size) => contentType switch
    {
        "image/jpeg" or "image/png" => size > WhatsAppMediaSender.MaxImageBytes ? WhatsAppConversationErrors.MediaInvalid : null,
        "application/pdf" => size > WhatsAppMediaSender.MaxDocumentBytes ? WhatsAppConversationErrors.MediaInvalid : null,
        _ => WhatsAppConversationErrors.MediaTypeNotAllowed,
    };
}

// ── Read ─────────────────────────────────────────────────────────────────────────────────────

/// <param name="MayForce">Whether the caller holds <c>WhatsApp.Send.Force</c> (the template cooldown), read from the token.</param>
internal sealed record GetProjectWhatsAppQuery(Guid ProjectId, bool MayForce = false) : IRequest<Result<ProjectWhatsAppDto>>;

internal sealed class GetProjectWhatsAppHandler(ProjectWhatsAppTarget target, ICurrentUser me, TimeProvider clock, ISender? sender = null)
    : IRequestHandler<GetProjectWhatsAppQuery, Result<ProjectWhatsAppDto>>
{
    public async Task<Result<ProjectWhatsAppDto>> Handle(GetProjectWhatsAppQuery query, CancellationToken ct)
    {
        // Other modules' stores are read on the way (the client, the channel): no ambient transaction to promote.
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var loaded = await target.LoadAsync(query.ProjectId, ct);
        if (loaded.IsFailure) return Result.Fail<ProjectWhatsAppDto>(loaded.Error!);

        var s = loaded.Value!;
        var c = s.Conversation;
        var files = await target.FilesAsync(s.Project.Id, ct);
        var (templateBlockers, templates) = await TemplatesAsync(s, query.MayForce, ct);

        suppressed.Complete();
        return Result.Ok(new ProjectWhatsAppDto(
            s.Recipient, s.RecipientId, c?.Id, c?.Status.ToString(),
            c is { Status: WhatsAppConversationStatus.Human } && c.OwnerUserId == me.Id,
            c?.IsWindowOpen(clock.GetUtcNow()) ?? false,
            c?.WindowExpiresAt,
            s.Blockers,
            files,
            templateBlockers,
            templates));
    }

    /// <summary>
    /// The record's own template reading — the lead's panel and compose, the client's panel — asked
    /// for the templates WITH a file: what the project page offers outside the window says exactly
    /// what the record's WhatsApp tab would.
    /// </summary>
    private async Task<(IReadOnlyList<string> Blockers, IReadOnlyList<WhatsAppComposeTemplateDto> Templates)> TemplatesAsync(
        ProjectWhatsAppState s, bool mayForce, CancellationToken ct)
    {
        if (sender is null || s.RecipientId is not { } id) return ([ProjectWhatsAppErrors.NoRecipient], []);

        if (s.Recipient == "Lead")
        {
            var panel = await sender.Send(new GetWhatsAppPanelQuery(id, HasWhatsAppPermission: true, MayForce: mayForce), ct);
            if (panel.IsFailure) return ([panel.Error!], []);
            var compose = await sender.Send(new ComposeWhatsAppQuery(id, HasWhatsAppPermission: true, WithFile: true), ct);
            return (panel.Value!.Blockers, compose.IsSuccess ? compose.Value! : []);
        }

        var client = await sender.Send(new GetClientWhatsAppQuery(id, mayForce, WithFile: true), ct);
        return client.IsSuccess ? (client.Value!.Blockers, client.Value.Templates) : ([client.Error!], []);
    }
}

// ── Send a template with a file (lot 3) ──────────────────────────────────────────────────────

/// <param name="MayForce">Whether the caller holds <c>WhatsApp.Send.Force</c>, read from the token by the endpoint.</param>
internal sealed record SendProjectWhatsAppTemplateCommand(
    Guid ProjectId, Guid TemplateId, Guid AttachmentId, IReadOnlyList<string> Variables, bool MayForce)
    : IRequest<Result<SendWhatsAppTemplateResult>>;

/// <summary>
/// Outside the 24-hour window, a project's devis leaves in an approved template whose DOCUMENT
/// (PDF) or IMAGE header carries it (project documents lot 3, product owner). The project's file
/// is read here, with the caller's rights, and handed to the RECORD'S OWN template command — the
/// lead's (consent, cooldown, product) or the client's (STOP, restriction, cooldown) — which checks,
/// keeps the file encrypted and queues the message, linked to the project.
///
/// <para>
/// Deliberately not an <c>ICommand</c>: the record's command it sends is one, and that is the
/// audited, transacted act — the file marked sensitive on it.
/// </para>
/// </summary>
internal sealed class SendProjectWhatsAppTemplateHandler(ProjectWhatsAppTarget target, ProjectSendingReader files, ICurrentUser me, ISender sender)
    : IRequestHandler<SendProjectWhatsAppTemplateCommand, Result<SendWhatsAppTemplateResult>>
{
    public async Task<Result<SendWhatsAppTemplateResult>> Handle(SendProjectWhatsAppTemplateCommand cmd, CancellationToken ct)
    {
        var resolved = await target.RecipientAsync(cmd.ProjectId, ct);
        if (resolved.IsFailure) return Result.Fail<SendWhatsAppTemplateResult>(resolved.Error!);
        var (project, recipient, recipientId) = resolved.Value;
        if (recipientId is not { } id) return Result.Fail<SendWhatsAppTemplateResult>(ProjectWhatsAppErrors.NoRecipient);

        var read = await files.ReadFilesAsync(me.TenantId, project.Id, [cmd.AttachmentId], me.Id, ct);
        if (read.IsFailure) return Result.Fail<SendWhatsAppTemplateResult>(read.Error!);
        var file = read.Value!.Single();
        var templateFile = new TemplateFile(file.Content, file.ContentType, file.FileName);
        var values = cmd.Variables ?? [];

        return recipient == "Lead"
            ? await sender.Send(new SendWhatsAppTemplateCommand(id, cmd.TemplateId, values, cmd.MayForce, templateFile, project.Id, file.Id), ct)
            : await sender.Send(new SendClientWhatsAppTemplateCommand(id, cmd.TemplateId, values, ComplaintId: null, cmd.MayForce, templateFile, project.Id, file.Id), ct);
    }
}

// ── Send ─────────────────────────────────────────────────────────────────────────────────────

internal sealed record SendProjectWhatsAppDocumentsCommand(
    Guid ProjectId, IReadOnlyList<Guid> AttachmentIds, [property: SensitiveData] string? Caption)
    : IRequest<Result<IReadOnlyList<ProjectWhatsAppSentDto>>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => ProjectId.ToString();
}

/// <summary>
/// Sends some of a project's files to its lead or client, one WhatsApp message each, the caption on
/// the first. Everything is checked BEFORE the first leaves — the recipient's rules, every file
/// the project's and carried by WhatsApp — so a Word file in the selection sends nothing rather
/// than half. Each message is linked to the project (<see cref="WhatsAppMessage.OpportunityId"/>).
///
/// <para>
/// <b>Outside the command's transaction</b>, like the mailbox's send: a provider call is seconds,
/// and a rollback after the second file left would erase the record of the first. Each message
/// commits as it is sent; a refusal by the provider is a Failed message, answered per file.
/// </para>
/// </summary>
internal sealed class SendProjectWhatsAppDocumentsHandler(
    ProjectWhatsAppTarget target, ProjectSendingReader files, WhatsAppMediaSender sender, ICurrentUser me,
    ProjectDocumentTrail? trail = null, LeadsDbContext? db = null)
    : IRequestHandler<SendProjectWhatsAppDocumentsCommand, Result<IReadOnlyList<ProjectWhatsAppSentDto>>>
{
    public async Task<Result<IReadOnlyList<ProjectWhatsAppSentDto>>> Handle(SendProjectWhatsAppDocumentsCommand cmd, CancellationToken ct)
    {
        var ids = (cmd.AttachmentIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > ProjectWhatsAppTarget.MaxFiles) return Fail(ProjectWhatsAppErrors.FilesInvalid);

        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var loaded = await target.LoadAsync(cmd.ProjectId, ct);
        if (loaded.IsFailure) return Fail(loaded.Error!);
        var s = loaded.Value!;
        if (s.Blockers.Count > 0) return Fail(s.Blockers[0]);

        var read = await files.ReadFilesAsync(me.TenantId, s.Project.Id, ids, me.Id, ct);
        if (read.IsFailure) return Fail(read.Error!);

        var checkedFiles = new List<(ProjectFileForSending File, WhatsAppMediaKind Media)>();
        foreach (var file in read.Value!)
        {
            var media = WhatsAppMediaSender.Check(file.Content, file.ContentType);
            if (media.IsFailure) return Fail(media.Error!);
            checkedFiles.Add((file, media.Value!));
        }

        var sent = new List<ProjectWhatsAppSentDto>(checkedFiles.Count);
        foreach (var (file, media) in checkedFiles)
        {
            var caption = sent.Count == 0 ? cmd.Caption : null;
            var result = await sender.SendAsync(s.Conversation!, media, file.Content, file.FileName, caption,
                message => message.LinkToProject(s.Project.Id, file.Id), ct);
            sent.Add(new ProjectWhatsAppSentDto(file.Id, result.MessageId, result.Status, result.ErrorCode));
        }

        // The project's trail (lot 4): one « Document envoyé » line for what the provider took.
        var left = sent.Where(r => r.Status == nameof(WhatsAppMessageStatus.Sent)).ToList();
        if (left.Count > 0 && trail is not null && db is not null)
        {
            await trail.RecordAsync(me.TenantId, s.Project.Id, ProjectDocumentChannels.WhatsApp,
                left.Select(r => r.AttachmentId).ToList(), me.Id, left.Count == 1 ? left[0].MessageId : null, ct);
            await db.SaveChangesAsync(ct);
        }

        suppressed.Complete();
        return Result.Ok<IReadOnlyList<ProjectWhatsAppSentDto>>(sent);
    }

    private static Result<IReadOnlyList<ProjectWhatsAppSentDto>> Fail(string code) => Result.Fail<IReadOnlyList<ProjectWhatsAppSentDto>>(code);
}

// ── Routes ───────────────────────────────────────────────────────────────────────────────────

internal static class ProjectWhatsAppEndpoints
{
    /// <summary>On the <c>opportunities</c> group.</summary>
    public static IEndpointRouteBuilder MapProjectWhatsApp(this IEndpointRouteBuilder group)
    {
        group.MapGet("{id:guid}/whatsapp", async (Guid id, HttpContext http, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetProjectWhatsAppQuery(id, MayForce(http)), ct)))
            .WithName("GetProjectWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("Who the project's documents go to by WhatsApp, in which conversation, what stops it now, and which files WhatsApp carries")
            .WithDescription(
                "The project's lead when it has one, else its client. `blockers` holds the refusal a send would get now "
                + "(WHATSAPP_NOT_HOLDER: take the conversation over first; WHATSAPP_WINDOW_CLOSED: outside the 24-hour window; "
                + "WHATSAPP_CONVERSATION_NOT_FOUND: the person never wrote; …). A file is sendable when it is a JPEG/PNG "
                + "(5 MB) or a PDF (10 MB); others go by e-mail only. 404 outside the project page's perimeter.")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<ProjectWhatsAppDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithTenantHeader();

        group.MapPost("{id:guid}/whatsapp/documents",
                async (Guid id, SendProjectWhatsAppDocumentsRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SendProjectWhatsAppDocumentsCommand(id, req.AttachmentIds ?? [], req.Caption), ct)))
            .WithName("SendProjectWhatsAppDocuments")
            .WithTags("WhatsApp")
            .WithSummary("Sends some of the project's files to its lead or client by WhatsApp, one message each, inside the 24-hour window")
            .WithDescription(
                "1 to 5 attachment ids of THIS project, the caption on the first message. Checked before anything leaves: "
                + "the recipient's rules (as GET …/whatsapp), every file the project's (PROJECT_ATTACHMENT_NOT_FOUND) and carried "
                + "by WhatsApp (WHATSAPP_MEDIA_TYPE_NOT_ALLOWED, WHATSAPP_MEDIA_INVALID). A provider refusal is answered "
                + "per file with status Failed. Requires WhatsApp.Reply.")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .Produces<IReadOnlyList<ProjectWhatsAppSentDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithTenantHeader();

        group.MapPost("{id:guid}/whatsapp/template",
                async (Guid id, SendProjectWhatsAppTemplateRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new SendProjectWhatsAppTemplateCommand(
                        id, req.TemplateId, req.AttachmentId, req.Variables ?? [], MayForce(http)), ct)))
            .WithName("SendProjectWhatsAppTemplate")
            .WithTags("WhatsApp")
            .WithSummary("Sends one of the project's files to its lead or client in an approved template with a DOCUMENT or IMAGE header — at any time")
            .WithDescription(
                "For outside the 24-hour window. The template's header takes the file: a PDF for DOCUMENT, a JPEG/PNG for IMAGE "
                + "(WHATSAPP_MEDIA_TYPE_NOT_ALLOWED otherwise; WHATSAPP_TEMPLATE_HAS_NO_FILE_HEADER for a template without one). "
                + "The record's own template rules apply (a lead's consent, a STOP, the 24-hour cooldown unless WhatsApp.Send.Force). "
                + "Queued like any template: the file is kept encrypted until it leaves (WHATSAPP_MEDIA_STORE_UNAVAILABLE when no "
                + "media store is configured). The message is linked to the project.")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<SendWhatsAppTemplateResult>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithTenantHeader();

        return group;
    }

    private static bool MayForce(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanForceWhatsAppSend.Code);

    private static IResult Map<T>(Result<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : result.Error is OpportunityErrors.NotFound or "PROJECT_ATTACHMENT_NOT_FOUND" or WhatsAppErrors.TemplateNotFound or WhatsAppErrors.LeadNotFound
            ? Results.Problem(title: "WhatsApp", detail: result.Error, statusCode: StatusCodes.Status404NotFound)
            : result.Error == "PROJECT_ATTACHMENTS_UNAVAILABLE"
                ? Results.Problem(title: "WhatsApp", detail: result.Error, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Problem(title: "WhatsApp", detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
}
