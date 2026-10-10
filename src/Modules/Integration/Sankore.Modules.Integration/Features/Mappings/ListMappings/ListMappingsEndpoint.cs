namespace Sankore.Modules.Integration.Features.Mappings.ListMappings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListMappingsEndpoint
{
    public static IEndpointRouteBuilder MapListMappings(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("ListIntegrationMappings")
            .WithSummary("List a connection's code mappings")
            .WithDescription(
                "Every mapping of the connection, ordered by domain then CRM code. Optional "
                + $"`domain` filter, one of: {MappingDomainRoute.Values}. "
                + "Un-paginated: a correspondence table is a closed list maintained by hand. "
                + "An unknown connection answers 404 INTEGRATION_CONNECTION_NOT_FOUND, and so does "
                + "a connection belonging to another tenant — never 403, so the response cannot be "
                + "used to probe for other tenants' connections. "
                // There is no Integration.Mapping.View in the specification's permission table:
                // the single Manage code gates reads as well as writes, deliberately, rather than
                // inventing a permission the RoleSeeder does not know about.
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
            .Produces<ListMappingsResponse>(StatusCodes.Status200OK)
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
        string? domain,
        ISender sender,
        CancellationToken ct)
    {
        MappingDomain? filter = null;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            if (!MappingDomainRoute.TryParse(domain, out var parsed))
                return Results.Problem(
                    $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                    statusCode: StatusCodes.Status400BadRequest);

            filter = parsed;
        }

        var result = await sender.Send(new ListMappingsQuery(connectionId, filter), ct);

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
