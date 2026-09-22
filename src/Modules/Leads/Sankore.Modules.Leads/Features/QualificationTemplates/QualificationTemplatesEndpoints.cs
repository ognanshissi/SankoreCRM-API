using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

namespace Sankore.Modules.Leads.Features.QualificationTemplates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.ArchiveQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.GetActiveTemplateForProduct;
using Sankore.Modules.Leads.Features.QualificationTemplates.ResolveQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.GetQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.ListQualificationTemplates;
using Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;
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

        group.MapGet("active/{productCategory}", GetActiveTemplate)
            .WithName("GetActiveTemplateForProduct")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualificationTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapGet("{templateId:guid}", GetTemplate)
            .WithName("GetQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualificationTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPut("{templateId:guid}", UpdateTemplate)
            .WithName("UpdateQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageQualificationTemplates.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("{templateId:guid}/publish", PublishTemplate)
            .WithName("PublishQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageQualificationTemplates.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapGet("resolve", ResolveTemplate)
            .WithName("ResolveQualificationTemplate")
            .WithTags("Leads")
            .WithSummary("Resolve the best qualification template for a product code (3-level fallback)")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualificationTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{templateId:guid}/archive", ArchiveTemplate)
            .WithName("ArchiveQualificationTemplate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageQualificationTemplates.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
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
            ProductCategory: req.ProductCategory,
            ProductCode: req.ProductCode,
            Questions:   req.Questions,
            Sections:    req.Sections), ct);

        if (!result.IsSuccess)
            return Results.Problem(title: "Create template failed", detail: result.Error, statusCode: 422);

        return Results.Created($"/api/v1/leads/qualification-templates/{result.Value}", result.Value);
    }

    private static async Task<IResult> ListTemplates(
        TemplateStatus? status,
        string? productCategory,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListQualificationTemplatesQuery(
            Status:      status,
            ProductCategory: productCategory), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetActiveTemplate(
        string productCategory,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetActiveTemplateForProductQuery(productCategory), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
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

    private static async Task<IResult> UpdateTemplate(
        Guid templateId,
        UpdateQualificationTemplateRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateQualificationTemplateCommand(
            TemplateId:  templateId,
            Name:        req.Name,
            Description: req.Description,
            ProductCategory: req.ProductCategory,
            ProductCode: req.ProductCode,
            Questions:   req.Questions,
            Sections:    req.Sections), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error is "TEMPLATE_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Update template failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> PublishTemplate(
        Guid templateId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new PublishQualificationTemplateCommand(templateId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error is "TEMPLATE_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Publish template failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ArchiveTemplate(
        Guid templateId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ArchiveQualificationTemplateCommand(templateId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error is "TEMPLATE_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Archive template failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ResolveTemplate(
        string productCode,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ResolveQualificationTemplateQuery(productCode), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}

public sealed record CreateQualificationTemplateRequest(
    string Name,
    string? Description,
    string? ProductCategory = null,
    string? ProductCode = null,
    IReadOnlyList<QuestionInput>? Questions = null,
    IReadOnlyList<SectionInput>? Sections = null);

public sealed record UpdateQualificationTemplateRequest(
    string Name,
    string? Description,
    string? ProductCategory = null,
    string? ProductCode = null,
    IReadOnlyList<QuestionInput>? Questions = null,
    IReadOnlyList<SectionInput>? Sections = null);
