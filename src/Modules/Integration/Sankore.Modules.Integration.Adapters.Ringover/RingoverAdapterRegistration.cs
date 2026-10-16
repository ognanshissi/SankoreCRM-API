namespace Sankore.Modules.Integration.Adapters.Ringover;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the Ringover adapter into the keyed registry <c>IntegrationAdapterResolver</c> looks a
/// connection's adapter up by (TEL-B-04).
///
/// <para>
/// <b>Registered even though it refuses almost every call</b>, and that is the useful shape.
/// Without the registration a configured Ringover connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> — "this deployment shipped without the
/// assembly", which sends an administrator to look at a build that is in fact correct. With it,
/// the same connection answers one of four refusals that each name their own owner: settings of
/// the wrong shape, no API host, a missing secret, or the missing test account. The first three
/// are the tenant's own screens and an afternoon's work; only the last is a procurement
/// conversation. One is a wild-goose chase, the others are actionable.
/// </para>
///
/// <para>
/// <b>And one member genuinely works through this registration</b>, which is new among the
/// blocked adapters: <c>ParseWebhook</c> is pure, needs no account, and produces a real
/// deduplication key — so a webhook arriving at a configured connection is recorded as received
/// rather than lost, and a redelivery of it is a no-op. That is only reachable if the adapter is
/// resolvable.
/// </para>
///
/// <para>
/// <b>Registered as <c>ICbsAdapter</c>, and that is not a mistake in a telephony adapter.</b>
/// This module has no <c>ITelephonyAdapter</c>: <c>IntegrationAdapterResolver.ResolveAdapter</c>
/// resolves a keyed <c>ICbsAdapter</c> from <c>connection.Kind</c> for every family, and
/// <c>ResolvePort</c> casts to <c>ITelephonyPort</c> afterwards. Registering under any other
/// service type — or under any key other than <c>IntegrationKind.Ringover.ToString()</c> — is a
/// registration no connection can reach, which is exactly the failure mode
/// <c>IntegrationAdapterCompositionTests</c> exists to catch.
/// </para>
///
/// <para>
/// <b>No environment gate</b>, unlike the in-memory double. That gate exists because the double
/// answers plausible successes with no provider at all, so a deployment reaching it would report
/// calls that never happened. Nothing here can do that: every operation refuses, which is as
/// correct in production as in Development.
/// </para>
/// </summary>
public static class RingoverAdapterRegistration
{
    /// <summary>
    /// Registers the adapter under <c>IntegrationKind.Ringover</c>.
    ///
    /// <para>
    /// It does NOT register the integration module, and it does not register the secrets vault:
    /// the adapter consumes both (<c>IntegrationDbContext</c> and <c>ITenantContext</c> to resolve
    /// the tenant's connection, <c>ISecretsModule</c> to prove its two secrets exist), so
    /// <c>AddIntegrationModule</c> and <c>AddSecretsVault</c> must already have run. Registering a
    /// vault from here would give one host two of them, and the second one's key would decrypt
    /// nothing.
    /// </para>
    /// </summary>
    /// <param name="config">
    /// Unused today, and part of the signature on purpose. Every adapter registration in this
    /// module takes it — the host calls them in one block — and this one will need it the moment
    /// the account is obtained and brings options worth binding: the per-call timeout, the
    /// response ceiling, the page size of the catch-up sweep, and a generated client's base path.
    /// Keeping the shape means the arrival of the account changes this file's body and not the
    /// host's composition. Validated for null so a caller that forgets it fails at boot rather
    /// than at the first call.
    /// </param>
    public static IServiceCollection AddRingoverAdapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The health answer stamps CheckedAt. TryAdd, because the module and the other adapters
        // register the same system clock and the last word on it must not depend on call order.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, like every adapter: it resolves the tenant's connection once per scope and
        // caches it (see RingoverAdapter.Bind), and a singleton would hand one tenant's
        // connection id — and therefore its vault keys — to the next tenant's Hangfire job.
        services.AddKeyedScoped<ICbsAdapter, RingoverAdapter>(IntegrationKind.Ringover.ToString());

        return services;
    }
}
