namespace Sankore.Modules.Integration.Features.Mappings.DeleteMapping;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DeleteMappingEndpoint
{
    public static IEndpointRouteBuilder MapDeleteMapping(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{domain}/{crmCode}", Handle)
            .WithName("DeleteIntegrationMapping")
            .WithSummary("Remove one code mapping")
            .WithDescription(
                $"`domain` is one of: {MappingDomainRoute.Values}. `crmCode` is URL-encoded. "
                + "Removing a mapping does NOT make the CRM code pass through to the external "
                + "system: the next command carrying it fails with INTEGRATION_MAPPING_MISSING, "
                + "naming the domain and the code. "
                + "An unknown connection or mapping — including one belonging to another tenant — "
                + "answers 404, never 403. "
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
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
        Guid connectionId,
        string domain,
        string crmCode,
        ISender sender,
        CancellationToken ct)
    {
        if (!MappingDomainRoute.TryParse(domain, out var parsed))
            return Results.Problem(
                $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(new DeleteMappingCommand(connectionId, parsed, crmCode), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound or IntegrationErrors.MappingNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity)
        };
    }
}
