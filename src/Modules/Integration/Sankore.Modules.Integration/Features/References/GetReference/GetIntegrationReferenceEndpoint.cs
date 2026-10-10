namespace Sankore.Modules.Integration.Features.References.GetReference;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetIntegrationReferenceEndpoint
{
    internal static IEndpointRouteBuilder MapGetIntegrationReference(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("GetIntegrationReference")
            .WithSummary("Read the external identifier of a CRM entity on one connection")
            .WithDescription(
                "Answers the identifier the external system assigned to a CRM entity, per "
                + "connection. externalId is null when that connection holds no reference for the "
                + "entity yet — a 200, not a 404, because an entity not yet created on the other "
                + "side is an ordinary state. Scoped to the caller's tenant: a reference of "
                + "another tenant reads as absent. Requires Integration.Command.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationCommand.Code)
            .Produces<IntegrationReferenceDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] Guid connectionId,
        [FromQuery] string entityType,
        [FromQuery] Guid crmId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new GetIntegrationReferenceQuery(connectionId, entityType, crmId), ct);

        // Nothing here can be "not found": an unknown entity is a null externalId, which the
        // handler returns as a success.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
