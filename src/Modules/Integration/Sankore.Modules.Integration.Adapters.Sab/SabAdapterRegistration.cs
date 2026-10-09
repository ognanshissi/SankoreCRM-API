namespace Sankore.Modules.Integration.Adapters.Sab;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the SAB adapter into the keyed registry <c>IntegrationAdapterResolver</c> looks a
/// connection's adapter up by (INT-32).
///
/// <para>
/// <b>Registered even though it refuses every call</b>, and that is the useful shape. Without the
/// registration a configured SAB connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> — "this deployment shipped without the
/// assembly", which sends an administrator to look at a build. With it, the same connection
/// answers one of three refusals that each name their own owner: an unscoped installation, a
/// missing API key, or the missing Open SAB catalogue. One is a wild-goose chase, the others are
/// an afternoon's work or a procurement conversation.
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
public static class SabAdapterRegistration
{
    /// <summary>
    /// Registers the adapter under <c>IntegrationKind.Sab</c>.
    ///
    /// <para>
    /// It does NOT register the integration module, and it does not register the secrets vault:
    /// the adapter consumes both (<c>IntegrationDbContext</c> and <c>ITenantContext</c> to resolve
    /// the installation, <c>ISecretsModule</c> to prove the API key exists), so
    /// <c>AddIntegrationModule</c> and <c>AddSecretsVault</c> must already have run. Registering a
    /// vault from here would give one host two of them, and the second one's key would decrypt
    /// nothing.
    /// </para>
    /// </summary>
    /// <param name="config">
    /// Unused today, and part of the signature on purpose. Every adapter registration in this
    /// module takes it — the host calls them in one block — and this one will need it the moment
    /// the catalogue arrives and brings options worth binding (the per-call timeout, the response
    /// ceiling, a generated client's base path). Keeping the shape means the arrival of the
    /// document changes this file's body and not the host's composition, which is the whole claim
    /// of this chantier. Validated for null so a caller that forgets it fails at boot rather than
    /// at the first command.
    /// </param>
    public static IServiceCollection AddSabAdapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // The health answer stamps CheckedAt. TryAdd, because the module and the other adapters
        // register the same system clock and the last word on it must not depend on call order.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, like every adapter: it resolves the tenant's connection once per scope and
        // caches it (see SabAdapter.Bind), and a singleton would hand one tenant's installation —
        // its entity, its connection id, and therefore its vault key — to the next tenant's
        // Hangfire job. On a multi-IMF network that is the exact mistake criterion 2 exists to
        // prevent, arriving through dependency injection instead of through a wrong setting.
        services.AddKeyedScoped<ICbsAdapter, SabAdapter>(IntegrationKind.Sab.ToString());

        return services;
    }
}
