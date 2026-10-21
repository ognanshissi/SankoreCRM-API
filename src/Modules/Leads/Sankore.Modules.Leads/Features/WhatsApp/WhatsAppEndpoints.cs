namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

/// <summary>
/// The lead record's WhatsApp surface (WA-BE-06, WA-BE-19) and the templates screen (WA-BE-04).
///
/// <para>
/// <b>Reading a thread takes <c>WhatsApp.Send</c> or <c>WhatsApp.Supervise</c></b>, checked here from
/// the token's claims — policies in this platform are one per permission and AND together, so an
/// OR is read off the claims the way <c>Telephony.Recording.Listen</c> is — and then the lead's own
/// ownership rule, in the handler. <c>WhatsApp.Send.Force</c> is read the same way and handed to the
/// command as a flag the body cannot set.
/// </para>
/// </summary>
public static class WhatsAppEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppEndpoints(this IEndpointRouteBuilder app)
    {
        // ── On a lead ────────────────────────────────────────────────────────

        app.MapGet("{leadId:guid}/whatsapp", async (Guid leadId, HttpContext http, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetWhatsAppPanelQuery(leadId, MayRead(http), MayForce(http), MayReply(http), MaySupervise(http)), ct)))
            .WithName("GetLeadWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("The lead's WhatsApp state: consent, number, channel, conversation, and why sending is blocked")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<WhatsAppPanelDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("{leadId:guid}/whatsapp/messages",
                async (Guid leadId, DateTimeOffset? before, int? limit, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ListWhatsAppMessagesQuery(leadId, MayRead(http), before, limit ?? 30), ct)))
            .WithName("ListLeadWhatsAppMessages")
            .WithTags("WhatsApp")
            .WithSummary("The lead's WhatsApp thread, newest first; pass nextBefore back as `before` for older messages")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<WhatsAppMessagePageDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/read", async (Guid leadId, HttpContext http, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new MarkWhatsAppReadCommand(leadId, MayRead(http)), ct)))
            .WithName("MarkLeadWhatsAppRead")
            .WithTags("WhatsApp")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        app.MapGet("{leadId:guid}/whatsapp/messages/{messageId:guid}/media",
                async (Guid leadId, Guid messageId, HttpContext http, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new GetWhatsAppMediaQuery(leadId, messageId, MayRead(http)), ct);

                    if (result.IsFailure) return Problem(result.Error!);

                    // Never cached by a browser or a proxy: it is a customer's document, fetched
                    // under a permission that may be withdrawn tomorrow.
                    http.Response.Headers.CacheControl = "no-store";

                    var media = result.Value!;
                    var inline = media.ContentType.StartsWith("image/", StringComparison.Ordinal);

                    return Results.File(
                        media.Content,
                        media.ContentType,
                        fileDownloadName: inline ? null : media.FileName ?? "piece-jointe");
                })
            .WithName("GetLeadWhatsAppMedia")
            .WithTags("WhatsApp")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("{leadId:guid}/whatsapp/compose", async (Guid leadId, HttpContext http, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new ComposeWhatsAppQuery(leadId, MayRead(http)), ct)))
            .WithName("ComposeLeadWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("The approved templates for this lead's product, with their variables pre-filled from the lead")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<IReadOnlyList<WhatsAppComposeTemplateDto>>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/send-template",
                async (Guid leadId, SendWhatsAppTemplateRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(
                        new SendWhatsAppTemplateCommand(leadId, req.TemplateId, req.Variables ?? [], MayForce(http)), ct);

                    return result.IsSuccess
                        ? Results.Accepted($"/api/v1/leads/{leadId}/whatsapp/messages", result.Value)
                        : Problem(result.Error!);
                })
            .WithName("SendLeadWhatsAppTemplate")
            .WithTags("WhatsApp")
            .WithSummary("Queues an approved template for the lead; the provider is called out of band")
            .WithDescription(
                "Refused with 422 and one of: WHATSAPP_LEAD_CLOSED, WHATSAPP_CONSENT_MISSING, "
                + "WHATSAPP_CONSENT_REVOKED, WHATSAPP_PHONE_INVALID, WHATSAPP_CHANNEL_INACTIVE, "
                + "WHATSAPP_TEMPLATE_COOLDOWN (another template less than 24 h ago without an answer — "
                + "WhatsApp.Send.Force lifts it), WHATSAPP_TEMPLATE_NOT_FOUND, WHATSAPP_TEMPLATE_NOT_APPROVED, "
                + "WHATSAPP_TEMPLATE_NOT_FOR_PRODUCT, WHATSAPP_VARIABLES_INVALID. The message is then "
                + "Queued; its status follows on the thread. Requires permission: WhatsApp.Send.")
            .RequireAuthorization(Permissions.CanSendWhatsApp.Code)
            .Produces<SendWhatsAppTemplateResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // ── Conversation actions (WA-BE-16, WA-BE-17, WA-FE-08) ──────────────

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/takeover",
                async (Guid leadId, Guid conversationId, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new TakeOverWhatsAppCommand(leadId, conversationId, MayReply(http)), ct)))
            .WithName("TakeOverLeadWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("Takes the conversation over: the caller becomes its only holder and its takeover SLA stops")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/messages",
                async (Guid leadId, Guid conversationId, SendWhatsAppTextRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new SendWhatsAppTextCommand(leadId, conversationId, req.Text, MayReply(http)), ct);
                    return result.IsSuccess ? Results.Accepted(value: result.Value) : Problem(result.Error!);
                })
            .WithName("SendLeadWhatsAppText")
            .WithTags("WhatsApp")
            .WithSummary("Queues a free text message — holder only, inside the 24-hour window")
            .WithDescription(
                "Refused with 422: WHATSAPP_NOT_HOLDER, WHATSAPP_WINDOW_CLOSED, WHATSAPP_CONSENT_REVOKED, "
                + "WHATSAPP_TEXT_INVALID. Requires permission: WhatsApp.Reply.")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .Produces<Guid>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/media",
                async (Guid leadId, Guid conversationId, IFormFile file, [FromForm] string? caption,
                       HttpContext http, ISender sender, CancellationToken ct) =>
                {
                    if (file is null || file.Length is 0 or > SendWhatsAppMediaHandler.MaxDocumentBytes)
                        return Problem(WhatsAppConversationErrors.MediaInvalid);

                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, ct);

                    var result = await sender.Send(new SendWhatsAppMediaCommand(
                        leadId, conversationId, buffer.ToArray(), file.ContentType, file.FileName, caption, MayReply(http)), ct);

                    return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
                })
            .WithName("SendLeadWhatsAppMedia")
            .WithTags("WhatsApp")
            .WithSummary("Sends an image (JPEG, PNG, 5 MB) or a PDF (10 MB) — holder only, inside the window")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<SendWhatsAppMediaResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/return-to-bot",
                async (Guid leadId, Guid conversationId, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ReturnWhatsAppToBotCommand(leadId, conversationId, MayReply(http)), ct)))
            .WithName("ReturnLeadWhatsAppToBot")
            .WithTags("WhatsApp")
            .RequireAuthorization(Permissions.CanReplyWhatsApp.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/close",
                async (Guid leadId, Guid conversationId, CloseWhatsAppConversationRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new CloseWhatsAppConversationCommand(leadId, conversationId, req.Reason, MayReply(http), MaySupervise(http)), ct)))
            .WithName("CloseLeadWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("Closes the conversation with a reason; the customer's next message reopens it")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/whatsapp/conversations/{conversationId:guid}/reassign",
                async (Guid leadId, Guid conversationId, ReassignWhatsAppConversationRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ReassignWhatsAppConversationCommand(leadId, conversationId, req.AgentId, MaySupervise(http)), ct)))
            .WithName("ReassignLeadWhatsApp")
            .WithTags("WhatsApp")
            .WithSummary("Hands the conversation to an agent of the supervisor's team")
            .RequireAuthorization(Permissions.CanSuperviseWhatsApp.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // ── Inbox (WA-FE-09) ─────────────────────────────────────────────────

        app.MapGet("whatsapp/conversations",
                async (WhatsAppInboxTab? tab, Guid? agentId, Guid? productLineId, bool? unreadOnly, string? cursor, int? limit,
                       HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new GetWhatsAppInboxQuery(
                        tab ?? WhatsAppInboxTab.ToHandle, agentId, productLineId, unreadOnly == true, cursor, limit ?? 20,
                        MayRead(http), MaySupervise(http)), ct)))
            .WithName("ListWhatsAppConversations")
            .WithTags("WhatsApp")
            .WithSummary("The caller's WhatsApp queue, by tab, sorted by urgency, within their perimeter")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<WhatsAppInboxDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        // ── Templates ────────────────────────────────────────────────────────

        // ── One conversation, by its id (WA-BE-19) ───────────────────────────

        app.MapGet("whatsapp/conversations/{conversationId:guid}",
                async (Guid conversationId, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new GetWhatsAppConversationQuery(conversationId, MayRead(http), MaySupervise(http)), ct)))
            .WithName("GetWhatsAppConversation")
            .WithTags("WhatsApp")
            .WithSummary("One conversation of the caller's perimeter — the way to open one filed on no lead")
            .RequireAuthorization()
            .Produces<WhatsAppConversationDetailDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("whatsapp/conversations/{conversationId:guid}/messages",
                async (Guid conversationId, DateTimeOffset? before, int? limit, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ListWhatsAppConversationMessagesQuery(conversationId, MayRead(http), MaySupervise(http), before, limit ?? 30), ct)))
            .WithName("ListWhatsAppConversationMessages")
            .WithTags("WhatsApp")
            .WithSummary("A conversation's messages, newest first; pass nextBefore back as `before` for older ones")
            .RequireAuthorization()
            .Produces<WhatsAppMessagePageDto>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("whatsapp/conversations/{conversationId:guid}/messages/{messageId:guid}/media",
                async (Guid conversationId, Guid messageId, HttpContext http, ISender sender, CancellationToken ct) =>
                {
                    var result = await sender.Send(new GetWhatsAppConversationMediaQuery(conversationId, messageId, MayRead(http), MaySupervise(http)), ct);
                    if (result.IsFailure) return Problem(result.Error!);

                    http.Response.Headers.CacheControl = "no-store";
                    var media = result.Value!;
                    var inline = media.ContentType.StartsWith("image/", StringComparison.Ordinal);
                    return Results.File(media.Content, media.ContentType, fileDownloadName: inline ? null : media.FileName ?? "piece-jointe");
                })
            .WithName("GetWhatsAppConversationMedia")
            .WithTags("WhatsApp")
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone)
            .WithOpenApi();

        app.MapPost("whatsapp/conversations/{conversationId:guid}/attach",
                async (Guid conversationId, AttachWhatsAppConversationRequest req, HttpContext http, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new AttachWhatsAppConversationCommand(conversationId, req.LeadId, MayRead(http), MaySupervise(http)), ct)))
            .WithName("AttachWhatsAppConversation")
            .WithTags("WhatsApp")
            .WithSummary("Files a conversation that is on no lead on a lead the caller may work")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        app.MapGet("whatsapp/metrics",
                async (DateTimeOffset? from, DateTimeOffset? to, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new GetWhatsAppMetricsQuery(from, to), ct)))
            .WithName("GetWhatsAppMetrics")
            .WithTags("WhatsApp")
            .WithSummary("Template response rate, escalations, takeover delay, bot resolution and estimated cost, over a period (default: the last 30 days)")
            .RequireAuthorization(Permissions.CanSuperviseWhatsApp.Code)
            .Produces<WhatsAppMetricsDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        app.MapGet("whatsapp/templates", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListWhatsAppTemplatesQuery(), ct)))
            .WithName("ListWhatsAppTemplates")
            .WithTags("WhatsApp")
            .RequireAuthorization(Permissions.CanConfigureWhatsApp.Code)
            .Produces<IReadOnlyList<WhatsAppTemplateDto>>()
            .WithOpenApi();

        app.MapPost("whatsapp/templates/sync", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SyncWhatsAppTemplatesCommand(), ct)))
            .WithName("SyncWhatsAppTemplates")
            .WithTags("WhatsApp")
            .WithSummary("Reads every active number's template catalogue from the provider now")
            .RequireAuthorization(Permissions.CanConfigureWhatsApp.Code)
            .Produces<WhatsAppTemplateSyncResult>()
            .WithOpenApi();

        app.MapPut("whatsapp/templates/{templateId:guid}",
                async (Guid templateId, ConfigureWhatsAppTemplateRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new ConfigureWhatsAppTemplateCommand(templateId, req), ct)))
            .WithName("ConfigureWhatsAppTemplate")
            .WithTags("WhatsApp")
            .WithSummary("Sets the products a template is offered for and the lead field behind each variable")
            .RequireAuthorization(Permissions.CanConfigureWhatsApp.Code)
            .Produces<WhatsAppTemplateDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static bool MayRead(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanSendWhatsApp.Code)
           || http.User.HasClaim("permission", Permissions.CanReplyWhatsApp.Code)
           || http.User.HasClaim("permission", Permissions.CanSuperviseWhatsApp.Code);

    private static bool MayReply(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanReplyWhatsApp.Code);

    private static bool MaySupervise(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanSuperviseWhatsApp.Code);

    private static bool MayForce(HttpContext http)
        => http.User.HasClaim("permission", Permissions.CanForceWhatsAppSend.Code);

    private static IResult Map<T>(Result<T> result)
        => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map(Result result)
        => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        WhatsAppErrors.LeadNotFound or WhatsAppErrors.MessageNotFound or WhatsAppErrors.TemplateNotFound
            or WhatsAppConversationErrors.ConversationNotFound
            or WhatsAppErrors.NoMedia => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status404NotFound),
        LeadAccessGuard.Forbidden or WhatsAppConversationErrors.NotSupervisor => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status403Forbidden),
        WhatsAppErrors.AlreadyAttached => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status409Conflict),
        WhatsAppErrors.MediaPurged => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status410Gone),
        WhatsAppMetricsErrors.PeriodInvalid => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status400BadRequest),
        IntegrationErrors.MessagingMediaRefused => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status415UnsupportedMediaType),
        _ => Results.Problem(title: "WhatsApp", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
