namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ApproveClientMergeEndpoint
{
    public static IEndpointRouteBuilder MapApproveClientMerge(this IEndpointRouteBuilder app)
    {
        app.MapPost("{mergeRequestId:guid}/approve", Handle)
            .WithName("ApproveClientMerge")
            .WithSummary("Approve and execute a client merge")
            .WithDescription(
                "Approves a pending merge request and executes it in one transaction. The approver " +
                "can never be the requester (SELF_APPROVAL_FORBIDDEN) and a request can only be " +
                "decided once (MERGE_ALREADY_DECIDED). Requires permission: customers:merge.")
            .RequireAuthorization(Permissions.CanMergeCustomers.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid mergeRequestId,
        ApproveClientMergeRequest? request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ApproveClientMergeCommand(mergeRequestId, request?.Comment), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            CustomerErrors.MergeRequestNotFound or CustomerErrors.ClientNotFound =>
                Results.NotFound(new { error = result.Error }),

            // Already decided or already merged: the caller acted on a stale view of the request.
            CustomerErrors.MergeAlreadyDecided or CustomerErrors.ClientAlreadyMerged =>
                Results.Conflict(new { error = result.Error }),

            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record ApproveClientMergeRequest(string? Comment);
