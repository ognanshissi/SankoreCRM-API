namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RejectClientMergeEndpoint
{
    public static IEndpointRouteBuilder MapRejectClientMerge(this IEndpointRouteBuilder app)
    {
        app.MapPost("{mergeRequestId:guid}/reject", Handle)
            .WithName("RejectClientMerge")
            .WithSummary("Reject a client merge request")
            .WithDescription(
                "Closes a pending merge request without merging. A reason is mandatory. The " +
                "requester cannot decide their own request (SELF_APPROVAL_FORBIDDEN). " +
                "Requires permission: customers:merge.")
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
        RejectClientMergeRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RejectClientMergeCommand(mergeRequestId, request.Reason), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            CustomerErrors.MergeRequestNotFound or CustomerErrors.ClientNotFound =>
                Results.NotFound(new { error = result.Error }),

            CustomerErrors.MergeAlreadyDecided =>
                Results.Conflict(new { error = result.Error }),

            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record RejectClientMergeRequest(string Reason);
