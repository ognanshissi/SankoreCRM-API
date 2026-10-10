namespace Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CheckConnectionHealthEndpoint
{
    internal static IEndpointRouteBuilder MapCheckConnectionHealth(this IEndpointRouteBuilder app)
    {
        app.MapPost("{connectionId:guid}/health-check", Handle)
            .WithName("CheckIntegrationConnectionHealth")
            .WithSummary("Call the external system and record whether it answered")
            .WithDescription(
                "Resolves the adapter registered for this connection's kind, calls its health "
                + "check and STORES the outcome on the connection — the date, the result and the "
                + "detail. A connection must have a passed check before it can be activated "
                + "(INT-03), so this is the endpoint that unlocks POST "
                + "connections/{connectionId}/activate. An unreachable system answers 200 with "
                + "isHealthy false: that is a recorded fact, not an error. A kind this deployment "
                + "ships no adapter for answers 422 INTEGRATION_ADAPTER_NOT_REGISTERED and "
                + "records nothing — it is a fact about our build, not about the far end. "
                + "Audited. Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces<ConnectionHealthDto>(StatusCodes.Status200OK)
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
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CheckConnectionHealthCommand(connectionId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}
