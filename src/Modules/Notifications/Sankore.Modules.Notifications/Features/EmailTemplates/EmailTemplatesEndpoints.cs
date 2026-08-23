namespace Sankore.Modules.Notifications.Features.EmailTemplates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Notifications.Features.EmailTemplates.ActivateEmailTemplate;
using Sankore.Modules.Notifications.Features.EmailTemplates.CreateEmailTemplate;
using Sankore.Modules.Notifications.Features.EmailTemplates.GetEmailTemplate;
using Sankore.Modules.Notifications.Features.EmailTemplates.ListEmailTemplates;
using Sankore.Modules.Notifications.Features.EmailTemplates.UpdateEmailTemplate;
using Sankore.Shared.Kernel;

internal static class EmailTemplatesEndpoints
{
    internal static IEndpointRouteBuilder MapEmailTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("email-templates").WithTags("Email Templates");

        // GET /email-templates
        group.MapGet("", async (
            string? templateKey,
            string? locale,
            bool? isActive,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new ListEmailTemplatesQuery(templateKey, locale, isActive), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error);
        })
        .RequireAuthorization(Permissions.CanReadEmailTemplates.Code);

        // GET /email-templates/{id}
        group.MapGet("{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new GetEmailTemplateQuery(id), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(result.Error);
        })
        .RequireAuthorization(Permissions.CanReadEmailTemplates.Code);

        // POST /email-templates
        group.MapPost("", async (CreateEmailTemplateRequest req, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new CreateEmailTemplateCommand(
                req.TemplateKey, req.Locale, req.Subject, req.HtmlBody, req.TextBody, req.IsGlobal), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/email-templates/{result.Value}", new { id = result.Value })
                : Results.UnprocessableEntity(result.Error);
        })
        .RequireAuthorization(Permissions.CanManageEmailTemplates.Code);

        // PUT /email-templates/{id}  — creates new version, deactivates previous
        group.MapPut("{id:guid}", async (
            Guid id,
            UpdateEmailTemplateRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(
                new UpdateEmailTemplateCommand(id, req.Subject, req.HtmlBody, req.TextBody), ct);
            return result.IsSuccess
                ? Results.Ok(new { id = result.Value })
                : Results.UnprocessableEntity(result.Error);
        })
        .RequireAuthorization(Permissions.CanManageEmailTemplates.Code);

        // PATCH /email-templates/{id}/activate
        group.MapMethods("{id:guid}/activate", ["PATCH"], async (
            Guid id, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new ActivateEmailTemplateCommand(id), ct);
            return result.IsSuccess ? Results.NoContent() : Results.UnprocessableEntity(result.Error);
        })
        .RequireAuthorization(Permissions.CanManageEmailTemplates.Code);

        return app;
    }

    // ─── Request bodies ─────────────────────────────────────────────────────

    private sealed record CreateEmailTemplateRequest(
        string TemplateKey,
        string Locale,
        string Subject,
        string HtmlBody,
        string? TextBody,
        bool IsGlobal = false);

    private sealed record UpdateEmailTemplateRequest(
        string Subject,
        string HtmlBody,
        string? TextBody);
}
