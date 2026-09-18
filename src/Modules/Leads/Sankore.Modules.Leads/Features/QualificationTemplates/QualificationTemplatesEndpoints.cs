namespace Sankore.Modules.Leads.Features.QualificationTemplates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.GetQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class QualificationTemplatesEndpoints
{
    public static IEndpointRouteBuilder MapQualificationTemplatesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("qualification-templates");

        group.MapPost(string.Empty, CreateTemplate)
            .WithName("CreateQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageQualificationTemplates.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapGet(string.Empty, ListTemplates)
            .WithName("ListQualificationTemplates")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<IReadOnlyList<QualificationTemplateDto>>(StatusCodes.Status200OK)
            .WithOpenApi();

        group.MapGet("{templateId:guid}", GetTemplate)
            .WithName("GetQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualificationTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> CreateTemplate(
        CreateQualificationTemplateRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        var result = await sender.Send(new CreateQualificationTemplateCommand(
            TenantId:    tenantId,
            Name:        req.Name,
            Description: req.Description,
            ProductName: req.ProductName,
            Questions:   req.Questions), ct);

        if (!result.IsSuccess)
            return Results.Problem(title: "Create template failed", detail: result.Error, statusCode: 422);

        return Results.Created($"/api/v1/leads/qualification-templates/{result.Value}", result.Value);
    }

    private static async Task<IResult> ListTemplates(
        bool activeOnly,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListQualificationTemplatesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetTemplate(
        Guid templateId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetQualificationTemplateQuery(templateId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}

/// <param name="Questions">Ordered list of questions; weight is normalised across all questions to derive a 0-100 score.</param>
public sealed record CreateQualificationTemplateRequest(
    string Name,
    string? Description,
    string? ProductName,
    IReadOnlyList<QuestionInput> Questions);
