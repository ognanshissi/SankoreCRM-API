namespace Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RejectDuplicateCandidateEndpoint
{
    public static IEndpointRouteBuilder MapRejectDuplicateCandidate(this IEndpointRouteBuilder app)
    {
        app.MapPost("{candidateId:guid}/reject", Handle)
            .WithName("RejectDuplicateCandidate")
            .WithSummary("Dismiss a duplicate candidate")
            .WithDescription(
                "Records that the two clients are distinct people. The pair is not proposed again " +
                "until one of the compared fields changes. A reason is mandatory and is kept in the " +
                "audit trail. Requires permission: customers:merge.")
            .RequireAuthorization(Permissions.CanMergeCustomers.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid candidateId,
        RejectDuplicateCandidateRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RejectDuplicateCandidateCommand(candidateId, request.Reason), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error is DuplicatesErrors.DuplicateCandidateNotFound or CustomerErrors.ClientNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record RejectDuplicateCandidateRequest(string Reason);
