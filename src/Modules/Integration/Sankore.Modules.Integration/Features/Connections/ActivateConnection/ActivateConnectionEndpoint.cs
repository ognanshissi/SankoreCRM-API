namespace Sankore.Modules.Integration.Features.Connections.ActivateConnection;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ActivateConnectionEndpoint
{
    internal static IEndpointRouteBuilder MapActivateConnection(this IEndpointRouteBuilder app)
    {
        app.MapPost("{connectionId:guid}/activate", Handle)
            .WithName("ActivateIntegrationConnection")
            .WithSummary("Make this connection the one the tenant's calls go through")
            .WithDescription(
                "Activation requires a PASSED health check: without one it answers 422 "
                + "INTEGRATION_CONNECTION_NOT_HEALTHY — call POST "
                + "connections/{connectionId}/health-check first. A tenant may have only ONE "
                + "active core banking connection (a customer cannot be created in two core "
                + "banking systems) and as many active insurance ones as it distributes for: a "
                + "second core banking activation answers 409 "
                + "INTEGRATION_CORE_BANKING_ALREADY_ACTIVE, whether it lost to a read or to the "
                + "partial unique index. Activating an already-active connection is a success. "
                + "Audited. Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
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
        var result = await sender.Send(new ActivateConnectionCommand(connectionId), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => Results.NotFound(new { error = result.Error }),
            IntegrationErrors.CoreBankingConnectionAlreadyActive
                or IntegrationErrors.ConcurrencyConflict => Results.Conflict(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}
