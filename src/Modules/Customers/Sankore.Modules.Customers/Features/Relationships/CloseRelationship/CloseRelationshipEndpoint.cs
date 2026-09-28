using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.CloseRelationship;

public static class CloseRelationshipEndpoint
{
    public static IEndpointRouteBuilder MapCloseRelationship(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{relationshipId:guid}", Handle)
            .WithName("CloseClientRelationship")
            .WithSummary("Close a client relationship")
            .WithDescription(
                "Dates the relationship's validTo and records the reason; the row is never " +
                "physically deleted and stays readable with includeClosed=true. A reciprocal " +
                "(Spouse) relationship is closed at the same time, and the dependentsCount of " +
                "every client involved is recomputed. Unknown id returns RELATIONSHIP_NOT_FOUND. " +
                "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
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
        Guid clientId,
        Guid relationshipId,
        string? reason,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CloseRelationshipCommand(clientId, relationshipId, reason), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : RelationshipHttp.ToProblem(result.Error);
    }
}
