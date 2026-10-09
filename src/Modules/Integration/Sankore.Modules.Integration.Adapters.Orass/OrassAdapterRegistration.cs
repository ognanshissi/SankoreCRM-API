namespace Sankore.Modules.Integration.Adapters.Orass;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the ORASS adapter into the keyed registry <c>IntegrationAdapterResolver</c> looks a
/// connection's adapter up by (ASS-06).
///
/// <para>
/// <b>Registered even though it refuses every call</b>, and that is the useful shape. Without the
/// registration a configured ORASS connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> — "this deployment shipped without the
/// assembly", which sends an administrator to look at a build. With it, the same connection
/// answers one of five refusals that each name their own owner: settings of the wrong shape, no
/// intermediary code, a mode that contradicts the insurer's coordinates, a missing credential, or
/// the missing ORASS specification and insurer agreement. The first four are the tenant's own
/// screens and an afternoon's work; only the last is a procurement conversation. One is a
/// wild-goose chase, the others are actionable.
/// </para>
///
/// <para>
/// <b>Registered as <c>ICbsAdapter</c>, and that is not a mistake in an insurance adapter.</b>
/// This module has no <c>IInsuranceAdapter</c>: <c>IntegrationAdapterResolver.ResolveAdapter</c>
/// resolves a keyed <c>ICbsAdapter</c> from <c>connection.Kind</c> for both families, and
/// <c>IntegrationModuleFacade</c>'s insurance gateway goes through it before casting to
/// <c>IInsurancePolicyPort</c> or <c>IInsuranceClaimPort</c>. Registering under any other service
/// type — or under any other key than <c>IntegrationKind.Orass.ToString()</c> — is a registration
/// no connection can reach, which is exactly the failure mode
/// <c>IntegrationAdapterCompositionTests</c> exists to catch.
/// </para>
///
/// <para>
/// <b>No environment gate</b>, unlike the in-memory double. That gate exists because the double
/// answers plausible successes with no back-office at all, so a deployment reaching it would
/// report writes that never happened — and in this family that means reporting a subscription an
/// insurer never accepted. Nothing here can do that: every method refuses, which is the safest
/// behaviour an adapter can have, and it is as correct in production as in Development.
/// </para>
/// </summary>
public static class OrassAdapterRegistration
{
    /// <summary>
    /// Registers the adapter under <c>IntegrationKind.Orass</c>.
    ///
    /// <para>
    /// It does NOT register the integration module, and it does not register the secrets vault:
    /// the adapter consumes both (<c>IntegrationDbContext</c> and <c>ITenantContext</c> to resolve
    /// the tenant's installations, <c>ISecretsModule</c> to prove the carrier's credential exists),
    /// so <c>AddIntegrationModule</c> and <c>AddSecretsVault</c> must already have run.
    /// Registering a vault from here would give one host two of them, and the second one's key
    /// would decrypt nothing.
    /// </para>
    /// </summary>
    /// <param name="config">
    /// Unused today, and part of the signature on purpose. Every adapter registration in this
    /// module takes it — the host calls them in one block — and this one will need it the moment
    /// the specification arrives and brings options worth binding (for the API carrier: the
    /// per-call timeout, the response ceiling, a generated client's base path; for the bordereau
    /// carrier: the acknowledgement directory naming and the file-size ceiling). Keeping the shape
    /// means the arrival of the document changes this file's body and not the host's composition,
    /// which is the whole claim of this chantier. Validated for null so a caller that forgets it
    /// fails at boot rather than at the first command.
    /// </param>
    public static IServiceCollection AddOrassAdapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The health answer stamps CheckedAt. TryAdd, because the module and the other adapters
        // register the same system clock and the last word on it must not depend on call order.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, like every adapter: it resolves the tenant's ORASS installations once per scope
        // and caches them (see OrassAdapter.Bind), and a singleton would hand one tenant's
        // installations — their branches, their intermediary codes, their connection ids and
        // therefore their vault keys — to the next tenant's Hangfire job. In this family that
        // would book one institution's subscription under another's apporteur code, arriving
        // through dependency injection instead of through a wrong setting.
        services.AddKeyedScoped<ICbsAdapter, OrassAdapter>(IntegrationKind.Orass.ToString());

        return services;
    }
}
