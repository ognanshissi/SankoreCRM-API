namespace Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListUnmappedCodesEndpoint
{
    public static IEndpointRouteBuilder MapListUnmappedCodes(this IEndpointRouteBuilder app)
    {
        app.MapGet("{domain}/unmapped", Handle)
            .WithName("ListUnmappedIntegrationCodes")
            .WithSummary("List the CRM codes of a domain that have no mapping")
            .WithDescription(
                $"`domain` is one of: {MappingDomainRoute.Values}. "
                + "ALWAYS read `availability` before trusting an empty list: `Complete` means "
                + "nothing is missing, `Partial` means part of the CRM list could not be reached, "
                + "and `Unavailable` means no module contract exposes that list at all — the answer "
                + "is then empty by construction, not reassuring. `source` says which, in one "
                + "sentence, and is meant to be shown. "
                + "Today only `Agency` is sourceable, and only partially; every other domain "
                + "answers `Unavailable`. Mapping, import and export work for all eight. "
                + "An unknown connection, or one belonging to another tenant, answers 404 — never 403. "
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
            .Produces<UnmappedCodesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        string domain,
        ISender sender,
        CancellationToken ct)
    {
        if (!MappingDomainRoute.TryParse(domain, out var parsed))
            return Results.Problem(
                $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(new ListUnmappedCodesQuery(connectionId, parsed), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
