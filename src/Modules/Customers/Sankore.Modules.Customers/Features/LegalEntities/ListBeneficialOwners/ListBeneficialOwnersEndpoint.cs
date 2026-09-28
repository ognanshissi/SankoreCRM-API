namespace Sankore.Modules.Customers.Features.LegalEntities.ListBeneficialOwners;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListBeneficialOwnersEndpoint
{
    public static IEndpointRouteBuilder MapListBeneficialOwners(this IEndpointRouteBuilder app)
    {
        app.MapGet("{clientId:guid}/beneficial-owners", Handle)
            .WithName("ListBeneficialOwners")
            .WithSummary("List the beneficial owners of a legal client")
            .WithDescription(
                "Returns the active owners; pass includeClosed=true to also get the closed " +
                "rows (historical structure). Identity documents and dates of birth of " +
                "external owners come back MASKED — the clear value is only served by the " +
                "audited reveal endpoint. A client outside the caller's agency perimeter " +
                "answers 404, never 403. Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<BeneficialOwnerDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        bool? includeClosed,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ListBeneficialOwnersQuery(clientId, includeClosed ?? false), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error == CustomerErrors.ClientNotFound
            ? Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
