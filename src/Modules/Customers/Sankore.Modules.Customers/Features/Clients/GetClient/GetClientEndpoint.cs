namespace Sankore.Modules.Customers.Features.Clients.GetClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetClientEndpoint
{
    public static IEndpointRouteBuilder MapGetClient(this IEndpointRouteBuilder app)
    {
        app.MapGet("{clientId:guid}", Handle)
            .WithName("GetClient")
            .WithSummary("Get one client record")
            .WithDescription(
                "Returns the full client record. Every protected field comes back MASKED "
                + "(identityDocumentNumberMasked, dateOfBirthMasked, declaredIncomeMasked, and each "
                + "contact point's valueMasked); the clear value is served only by "
                + "POST clients/{clientId}/reveal, which is rate-limited and audited. "
                + "Includes the active contact points and the 5 most recent status transitions. "
                + "A merged client carries mergedInto = { clientId, clientNumber } so the caller can "
                + "follow the surviving record. "
                + "Echo the returned version as expectedVersion on the next PATCH. "
                + "A client that does not exist OR lies outside the caller's agency perimeter answers "
                + "404 CLIENT_NOT_FOUND — never 403, so the response cannot be used to probe for the "
                + "existence of clients in other branches. "
                + "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetClientQuery(clientId), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.ClientNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
