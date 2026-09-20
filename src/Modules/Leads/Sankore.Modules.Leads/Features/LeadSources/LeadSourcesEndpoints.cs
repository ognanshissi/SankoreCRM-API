namespace Sankore.Modules.Leads.Features.LeadSources;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.DeactivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;
using Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;
using Sankore.Shared.Kernel;

public static class LeadSourcesEndpoints
{
    public static IEndpointRouteBuilder MapLeadSourcesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("lead-sources");

        // GET /lead-sources
        group.MapGet("", ListSources)
            .WithName("ListLeadSources")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces<IReadOnlyList<LeadSourceDto>>()
            .WithOpenApi();

        // POST /lead-sources
        group.MapPost("", CreateSource)
            .WithName("CreateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT /lead-sources/{id}
        group.MapPut("{id:guid}", UpdateSource)
            .WithName("UpdateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /lead-sources/{id}/activate
        group.MapPost("{id:guid}/activate", ActivateSource)
            .WithName("ActivateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /lead-sources/{id}/deactivate
        group.MapPost("{id:guid}/deactivate", DeactivateSource)
            .WithName("DeactivateLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListSources(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListLeadSourcesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateSource(
        CreateLeadSourceRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateLeadSourceCommand(
            TenantId:     tenant.CurrentTenantId,
            Code:         req.Code,
            Label:        req.Label,
            DisplayOrder: req.DisplayOrder,
            Description:  req.Description), ct);

        return result.IsSuccess
            ? Results.Created($"lead-sources/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateSource(
        Guid id,
        UpdateLeadSourceRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateLeadSourceCommand(
            SourceId:     id,
            Code:         req.Code,
            Label:        req.Label,
            DisplayOrder: req.DisplayOrder,
            Description:  req.Description), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_SOURCE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateSource(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateLeadSourceCommand(id), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateSource(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateLeadSourceCommand(id), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "CANNOT_DEACTIVATE_SYSTEM_SOURCE"
                ? Results.Problem(detail: result.Error, statusCode: 422)
                : Results.NotFound();
    }
}

public sealed record CreateLeadSourceRequest(
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null);

public sealed record UpdateLeadSourceRequest(
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null);
