namespace Sankore.Modules.Integration.Features.Connections.DeactivateConnection;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class DeactivateConnectionEndpoint
{
    internal static IEndpointRouteBuilder MapDeactivateConnection(this IEndpointRouteBuilder app)
    {
        app.MapPost("{connectionId:guid}/deactivate", Handle)
            .WithName("DeactivateIntegrationConnection")
            .WithSummary("Stop routing this tenant's calls through the connection")
            .WithDescription(
                "Switches the connection off. It is NOT deleted: commands, identifier references "
                + "and call logs point at it, so a deleted row would make a year of audit trail "
                + "unreadable — there is no DELETE in this area. While no connection of a family "
                + "is active, every command for it answers "
                + "INTEGRATION_NO_ACTIVE_CONNECTION. Deactivating an already-inactive connection "
                + "is a success. Audited. "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateConnectionCommand(connectionId), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.Conflict(new { error = result.Error }),
        };
    }
}
