namespace Sankore.Modules.Leads.Features.Bulk;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Bulk.BulkAssignOwner;
using Sankore.Modules.Leads.Features.Bulk.BulkClose;
using Sankore.Modules.Leads.Features.Bulk.BulkRecycle;
using Sankore.Modules.Leads.Features.Bulk.BulkTag;
using Sankore.Shared.Kernel;

public static class BulkEndpoints
{
    public static IEndpointRouteBuilder MapBulkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("bulk");

        // POST leads/bulk/assign-owner
        group.MapPost("assign-owner", AssignOwner)
            .WithName("BulkAssignLeadOwner")
            .WithTags("Leads — Bulk")
            .RequireAuthorization(Permissions.CanAssignLead.Code)
            .Produces<BulkOperationResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/bulk/close
        group.MapPost("close", Close)
            .WithName("BulkCloseLeads")
            .WithTags("Leads — Bulk")
            .RequireAuthorization(Permissions.CanCloseLead.Code)
            .Produces<BulkOperationResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/bulk/tag
        group.MapPost("tag", Tag)
            .WithName("BulkTagLeads")
            .WithTags("Leads — Bulk")
            .RequireAuthorization(Permissions.CanTagLead.Code)
            .Produces<BulkOperationResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/bulk/recycle
        group.MapPost("recycle", Recycle)
            .WithName("BulkRecycleLeads")
            .WithTags("Leads — Bulk")
            .RequireAuthorization(Permissions.CanRecycleLead.Code)
            .Produces<BulkOperationResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> AssignOwner(
        BulkAssignOwnerRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new BulkAssignOwnerCommand(req.LeadIds, req.OwnerId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Bulk assign failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> Close(
        BulkCloseRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new BulkCloseCommand(req.LeadIds, req.Reason, req.Detail), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Bulk close failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> Tag(
        BulkTagRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new BulkTagCommand(req.LeadIds, req.Tag, req.AddedBy), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Bulk tag failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> Recycle(
        BulkRecycleRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new BulkRecycleCommand(req.LeadIds, req.NewSource, req.NewCampaign), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Bulk recycle failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record BulkAssignOwnerRequest(IReadOnlyList<Guid> LeadIds, Guid OwnerId);

public sealed record BulkCloseRequest(
    IReadOnlyList<Guid> LeadIds,
    LeadCloseReason Reason,
    string? Detail = null);

public sealed record BulkTagRequest(
    IReadOnlyList<Guid> LeadIds,
    string Tag,
    Guid AddedBy);

public sealed record BulkRecycleRequest(
    IReadOnlyList<Guid> LeadIds,
    LeadSource? NewSource = null,
    string? NewCampaign = null);
