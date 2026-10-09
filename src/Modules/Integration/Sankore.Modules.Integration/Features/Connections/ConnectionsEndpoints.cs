namespace Sankore.Modules.Integration.Features.Connections;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Connections.ActivateConnection;
using Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.Features.Connections.DeactivateConnection;
using Sankore.Modules.Integration.Features.Connections.GetConnection;
using Sankore.Modules.Integration.Features.Connections.GetConnectionSecretStatus;
using Sankore.Modules.Integration.Features.Connections.ListConnections;
using Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice (INT-03).
///
/// <para>
/// The lifecycle is deliberately three steps — create, health-check, activate — and not one.
/// Activation is what makes a connection the destination of every write the tenant queues, so it
/// may only follow a check that actually reached the far end.
/// </para>
///
/// <para>
/// INT-03's criterion covers the connection, its secrets AND its relay agent, but no route here
/// writes <c>RelayAgentId</c>: that link is server-set by INT-27's enrolment endpoint, which owns
/// the agent's identity and can guarantee the tenant match when it mints the certificate. It
/// stays under the same permission, <c>Integration.Connection.Manage</c>, so the criterion holds
/// — what moves is WHERE the field is written, because an id accepted from a request body could
/// point one tenant's writes at another tenant's on-premise network.
/// </para>
/// </summary>
internal static class ConnectionsEndpoints
{
    internal static IEndpointRouteBuilder MapIntegrationConnectionsEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/connections").WithTags("Integration");

        group.MapListConnections();
        group.MapGetConnection();
        group.MapCreateConnection();
        group.MapUpdateConnection();

        // Credentials: one write route, one "is it configured" route, and no route that returns a
        // value.
        group.MapSetConnectionSecret();
        group.MapGetConnectionSecretStatus();

        // Lifecycle.
        group.MapCheckConnectionHealth();
        group.MapActivateConnection();
        group.MapDeactivateConnection();

        return app;
    }
}
