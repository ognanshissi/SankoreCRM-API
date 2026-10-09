namespace Sankore.Modules.Integration.Features.RelayAgents.RevokeRelayAgent;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class RevokeRelayAgentEndpoint
{
    internal static IEndpointRouteBuilder MapRevokeRelayAgent(this IEndpointRouteBuilder app)
    {
        app.MapPost("{agentId:guid}/revoke", Handle)
            .WithName("RevokeIntegrationRelayAgent")
            .WithSummary("Revoke a relay agent and invalidate its certificate")
            .WithDescription(
                "Cuts the agent off: its certificate stops being admitted and an enrolment token "
                + "still armed is burned. The row is NOT deleted — connections, call logs and "
                + "batch files point at it, so a deleted agent would make a year of audit trail "
                + "unreadable. Revoking an already-revoked agent is a success and does not "
                + "overwrite who revoked it first. Takes effect for every message the agent "
                + "channel checks from this instant; the channel re-checks admission per message, "
                + "so there is no cached window during which a revoked certificate still works. "
                + "An agent of another tenant answers 404, never 403. Audited. "
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
        Guid agentId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new RevokeRelayAgentCommand(agentId), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            IntegrationErrors.RelayAgentNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.Conflict(new { error = result.Error }),
        };
    }
}
