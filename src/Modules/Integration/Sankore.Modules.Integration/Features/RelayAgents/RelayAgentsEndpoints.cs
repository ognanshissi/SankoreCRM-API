using Microsoft.AspNetCore.Http;

namespace Sankore.Modules.Integration.Features.RelayAgents;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.RelayAgents.ListRelayAgents;
using Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;
using Sankore.Modules.Integration.Features.RelayAgents.RevokeRelayAgent;

/// <summary>
/// Area aggregator for the routes a HUMAN calls (INT-27). One MapGroup, one call per slice.
///
/// <para>
/// <b>Mounted under <c>api/v1</c></b> by <c>IntegrationModule.MapIntegrationModuleEndpoints</c>,
/// like every other area of this module. Every route here carries
/// <c>Integration.Connection.Manage</c> — criterion 4 — and <c>.WithTenantHeader()</c>.
/// </para>
///
/// <para>
/// The agent's own two routes are NOT here. They are in
/// <see cref="RelayAgentPublicEndpoints"/>, a separate aggregator on a separate group, because
/// the machine presenting an enrolment token or a heartbeat has no JWT and no tenant header: a
/// group whose routes are a mix of authenticated and anonymous is one <c>.RequireAuthorization</c>
/// away from either breaking the agent or exposing an operator's route, and which of the two would
/// not be visible from the group's declaration.
/// </para>
/// </summary>
internal static class RelayAgentsEndpoints
{
    internal static IEndpointRouteBuilder MapRelayAgentsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/relay-agents").WithTags("Integration");

        // Criterion 3's read half: last contact, version, state. No thumbprint, no token.
        group.MapListRelayAgents();

        // Criterion 1: mints the single-use, short-lived token. The only route in the module that
        // returns a credential, and it returns it once.
        group.MapRegisterRelayAgent();

        // Criterion 2: clears the certificate so IRelayAgentAdmission stops admitting it.
        group.MapRevokeRelayAgent();

        return app;
    }
}
