namespace Sankore.Modules.Integration.Adapters.PerfectVision;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the Perfect Vision adapter into the keyed registry
/// <c>IntegrationAdapterResolver</c> looks a connection's adapter up by (INT-28).
///
/// <para>
/// <b>Registered even though it refuses every call</b>, and that is the useful shape. Without the
/// registration a configured Perfect Vision connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> — "this deployment shipped without the
/// assembly", which sends an administrator to look at a build. With it, the same connection
/// answers <see cref="IntegrationErrors.AdapterSpecificationPending"/> naming the missing
/// document, and the capability matrix of INT-28's criterion 3 becomes readable by a screen. One
/// is a wild-goose chase, the other is a procurement conversation.
/// </para>
///
/// <para>
/// <b>No environment gate</b>, unlike the in-memory double. That gate exists because the double
/// answers plausible successes with no back-office at all, so a deployment reaching it would
/// report writes that never happened. Nothing here can do that: every method refuses, which is
/// the safest behaviour an adapter can have, and it is as correct in production as in
/// Development.
/// </para>
/// </summary>
public static class PerfectVisionAdapterRegistration
{
    /// <summary>
    /// Registers the adapter under <c>IntegrationKind.PerfectVision</c>.
    ///
    /// <para>
    /// It does NOT register the integration module: the adapter consumes that module's services
    /// (<c>IntegrationDbContext</c> and <c>ITenantContext</c>, to read the installation's
    /// settings for the capability matrix), so <c>AddIntegrationModule</c> must already have run.
    /// </para>
    /// </summary>
    /// <param name="config">
    /// Unused today, and part of the signature on purpose. Every adapter registration in this
    /// module takes it — the host calls them in one block — and this one will need it the moment
    /// the specification arrives and brings options worth binding (the cut-off, the acknowledgement
    /// directory naming, the file-size ceiling). Keeping the shape means the arrival of the
    /// document changes this file's body and not the host's composition, which is the whole claim
    /// of this chantier. Validated for null so a caller that forgets it fails at boot rather than
    /// at the first command.
    /// </param>
    public static IServiceCollection AddPerfectVisionAdapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The health answer stamps CheckedAt. TryAdd, because the module and the other adapters
        // register the same system clock and the last word on it must not depend on call order.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, like every adapter: it caches the tenant's settings for the scope (see
        // PerfectVisionAdapter.Settings), and a singleton would hand one tenant's capability
        // matrix — including whether a live balance view exists — to the next tenant's job.
        services.AddKeyedScoped<ICbsAdapter, PerfectVisionAdapter>(
            IntegrationKind.PerfectVision.ToString());

        return services;
    }
}
