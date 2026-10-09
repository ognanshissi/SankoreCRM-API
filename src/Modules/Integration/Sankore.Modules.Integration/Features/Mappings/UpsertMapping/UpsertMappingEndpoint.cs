namespace Sankore.Modules.Integration.Features.Mappings.UpsertMapping;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpsertMappingEndpoint
{
    public static IEndpointRouteBuilder MapUpsertMapping(this IEndpointRouteBuilder app)
    {
        app.MapPut("{domain}/{crmCode}", Handle)
            .WithName("UpsertIntegrationMapping")
            .WithSummary("Create or update one code mapping")
            .WithDescription(
                $"`domain` is one of: {MappingDomainRoute.Values}. `crmCode` is the CRM's own code, "
                + "URL-encoded. Creating and updating are the same call: the identity of a mapping "
                + "is (connection, domain, crmCode), so re-sending one is an update and never a "
                + "conflict. Two CRM codes MAY share one external code — that is how several CRM "
                + "products fold onto one CBS product. "
                + "An unknown connection, or one belonging to another tenant, answers 404 "
                + "INTEGRATION_CONNECTION_NOT_FOUND — never 403. "
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
            .Produces<UpsertMappingResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        string domain,
        string crmCode,
        UpsertMappingRequest request,
        ISender sender,
        CancellationToken ct)
    {
        if (!MappingDomainRoute.TryParse(domain, out var parsed))
            return Results.Problem(
                $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(
            new UpsertMappingCommand(connectionId, parsed, crmCode, request.ExternalCode, request.Label), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity)
        };
    }
}
