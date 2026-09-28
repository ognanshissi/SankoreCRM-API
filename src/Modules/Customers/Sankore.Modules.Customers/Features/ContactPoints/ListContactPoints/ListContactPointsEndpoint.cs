using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.ListContactPoints;

public static class ListContactPointsEndpoint
{
    public static IEndpointRouteBuilder MapListContactPoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListClientContactPoints")
            .WithSummary("List a client's contact points")
            .WithDescription(
                "Values are returned masked (e.g. \"+225 07 •• •• 18\"); the clear value is only served " +
                "by the audited reveal endpoint. Pass includeClosed=true to also return " +
                "historical contact points (those with a validTo date). " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<ContactPointDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        bool includeClosed,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListContactPointsQuery(clientId, includeClosed), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ContactPointHttp.ToProblem(result.Error);
    }
}
