using Microsoft.AspNetCore.Http;

namespace Sankore.Modules.Integration.Features.RelayAgents;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;
using Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;

/// <summary>
/// The two routes the relay AGENT calls itself (INT-27).
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — one line, in <c>IntegrationModule.MapIntegrationPublicEndpoints</c></b>,
/// next to the existing <c>app.MapIntegrationWebhookEndpoints()</c>:
/// </para>
/// <code>
/// app.MapRelayAgentPublicEndpoints();
/// </code>
/// <para>
/// That method is the module's one public surface for anonymous routes and the bootstrapper
/// already calls it on the ROOT application, <b>outside <c>api/v1</c></b> and <b>before
/// <c>app.UseAuthentication()</c></b> — which is where these two routes must end up, exactly where
/// INT-20's webhook and M13's public ingest are. Nothing new is needed in <c>Program.cs</c>.
/// </para>
/// <para>
/// Outside <c>api/v1</c> because the caller is a machine installed inside an institution's
/// network, with no version contract with the front end and a URL baked into its configuration at
/// install time. Before <c>UseAuthentication</c> for the reason the webhook is: an anonymous
/// endpoint behind the authentication middleware still pays for a token it will never have, and a
/// stray <c>Authorization</c> header from an agent's HTTP stack would fail the request before the
/// token or the certificate is ever looked at.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <b>Anonymous is not unauthenticated.</b> The enrolment route is authenticated by the single-use
/// token, which is 256 bits of entropy and the only thing that selects a row; the heartbeat route
/// is authenticated by the certificate thumbprint the agent was admitted with. Neither accepts an
/// agent id or a tenant id from its body — the tenant is an OUTPUT of the exchange, which is the
/// whole of docs/integration-module-plan.md §5bis (a).
/// </para>
///
/// <para>
/// Both answer <c>401</c> with one code on every refusal, so the surface cannot be walked to learn
/// which agents exist. A separate aggregator from
/// <see cref="RelayAgentsEndpoints"/> rather than a second branch inside it: these two routes have
/// no <c>.RequireAuthorization</c> and no tenant header, and mixing them into the operator group
/// would make that difference invisible at the point where it matters.
/// </para>
/// </summary>
internal static class RelayAgentPublicEndpoints
{
    internal static IEndpointRouteBuilder MapRelayAgentPublicEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Same path as the authenticated group, one level up: an agent's URL is
        // /integration/relay-agents/enrol with no api/v1 prefix, which is deliberate — the agent
        // is pinned to a URL at install time and must not be re-deployed because the front end's
        // API version moved.
        var group = app.MapGroup("/integration/relay-agents").WithTags("Integration");

        // Criterion 1, the agent's half: presents the token, pins its certificate.
        group.MapExchangeEnrolmentToken();

        // Criterion 3, the ingestion half: the agent reports in.
        group.MapRecordRelayHeartbeat();

        return app;
    }
}
