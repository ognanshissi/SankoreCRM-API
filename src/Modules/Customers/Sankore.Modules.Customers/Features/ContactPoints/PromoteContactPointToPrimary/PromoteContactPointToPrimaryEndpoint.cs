using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

public static class PromoteContactPointToPrimaryEndpoint
{
    public static IEndpointRouteBuilder MapPromoteContactPointToPrimary(this IEndpointRouteBuilder app)
    {
        app.MapPost("{contactPointId:guid}/primary", Handle)
            .WithName("PromoteClientContactPointToPrimary")
            .WithSummary("Make a contact point the primary one of its type")
            .WithDescription(
                "Exactly one active primary contact point is allowed per type, so the previous " +
                "primary of the same type is demoted in the same transaction. A closed contact " +
                "point cannot be promoted. Requires permission: customers:update.")
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
        Guid contactPointId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new PromoteContactPointToPrimaryCommand(clientId, contactPointId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : ContactPointHttp.ToProblem(result.Error);
    }
}
