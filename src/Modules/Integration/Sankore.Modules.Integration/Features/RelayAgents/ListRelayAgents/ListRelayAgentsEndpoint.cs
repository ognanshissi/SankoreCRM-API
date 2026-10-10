namespace Sankore.Modules.Integration.Features.RelayAgents.ListRelayAgents;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListRelayAgentsEndpoint
{
    internal static IEndpointRouteBuilder MapListRelayAgents(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListIntegrationRelayAgents")
            .WithSummary("The tenant's relay agents, with their last contact, version and state")
            .WithDescription(
                "One row per on-premise relay agent of the current tenant: its state (Pending, "
                + "Active, Revoked), when it last reported in, the version and the latency it "
                + "reported, and when its certificate was admitted. Live agents first. No "
                + "certificate thumbprint is returned — it is not a secret, but it is the "
                + "identifier the agent channel authenticates on, and an operator needs to know "
                + "whether the relay is up, not what it authenticates with. The enrolment token "
                + "is not returned either: only its hash is stored, so no endpoint can show it "
                + "after the call that minted it. "
                + "Requires permission: Integration.Connection.Manage.")
            // Manage and not View, like the connection secret-status route: an agent's enrolment
            // state is operational information for whoever may register or revoke one.
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces<IReadOnlyList<RelayAgentDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ListRelayAgentsQuery(), ct)).Value);
}
