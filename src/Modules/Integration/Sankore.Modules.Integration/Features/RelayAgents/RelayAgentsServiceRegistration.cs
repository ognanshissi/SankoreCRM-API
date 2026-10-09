namespace Sankore.Modules.Integration.Features.RelayAgents;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the relay-agents area needs in the container (INT-27).
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>WIRING REQUIRED — three lines, none of them in this folder. INT-27 is inert without the
/// first two, and every test in the module still passes.</b>
/// </para>
///
/// <para>
/// <b>1. The module composition — <c>IntegrationModule.AddIntegrationModule</c>.</b> Next to the
/// other per-area registrations:
/// </para>
/// <code>
/// services.AddRelayAgentsServices();
/// </code>
///
/// <para>
/// <b>2. The operator routes — <c>IntegrationModule.MapIntegrationModuleEndpoints</c>.</b> Next to
/// the other area aggregators, so they land under <c>api/v1</c>:
/// </para>
/// <code>
/// app.MapRelayAgentsEndpoints();
/// </code>
///
/// <para>
/// <b>3. The agent's own routes — <c>IntegrationModule.MapIntegrationPublicEndpoints</c>.</b> See
/// <see cref="RelayAgentPublicEndpoints"/> for why they cannot share the group above:
/// </para>
/// <code>
/// app.MapRelayAgentPublicEndpoints();
/// </code>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <b>And one obligation that is not a line of wiring</b>, because the code it belongs to does not
/// exist yet: whatever hosts the relay agent channel (INT-26) must call
/// <see cref="IRelayAgentAdmission.AdmitAsync"/> on <b>every message</b>, not once at connect
/// time. That is what makes criterion 2's "revocation cuts the session immediately" true —
/// revoking clears the thumbprint here, but nothing in this module can close a socket it does not
/// own. The interface is public for exactly that reason.
/// </para>
/// </summary>
internal static class RelayAgentsServiceRegistration
{
    internal static IServiceCollection AddRelayAgentsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, because it reads through IntegrationDbContext, which is scoped. A singleton
        // would capture a disposed context, and a transient would be a pointless allocation per
        // message on the hottest path this area has.
        services.AddScoped<IRelayAgentAdmission, RelayAgentAdmission>();

        // The handlers and validators of this area are found by the assembly scans in
        // AddIntegrationModule (MediatR, and FluentValidation with includeInternalTypes), so
        // nothing else belongs here. TimeProvider, IntegrationDbContext and ICurrentUser are
        // already module- or host-provided.
        return services;
    }
}
