namespace Sankore.Modules.Integration.Adapters.Temenos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the Temenos adapter into the keyed registry <c>IntegrationAdapterResolver</c> looks a
/// connection's adapter up by (INT-12, INT-13).
///
/// <para>
/// No environment gate, unlike the in-memory double: this adapter talks to a real installation,
/// and a deployment that ships it without a configured connection simply resolves nothing — the
/// resolver answers <see cref="IntegrationErrors.NoActiveConnection"/>, which is the honest
/// state. The double's gate exists because it answers plausible successes with no back-office at
/// all; nothing here can report a write that did not happen.
/// </para>
/// </summary>
public static class TemenosAdapterRegistration
{
    /// <summary>
    /// Registers the adapter, its transport, its token cache and its named <c>HttpClient</c>.
    ///
    /// <para>
    /// Idempotent in the sense that matters: the keyed registration is added once per call, so the
    /// host calls it once. It does NOT register the integration module — the adapter is a
    /// consumer of that module's services (<c>IntegrationDbContext</c>, <c>MappingResolver</c>,
    /// <c>ICallJournal</c>, the secrets vault), so <c>AddIntegrationModule</c> must already have
    /// run.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTemenosAdapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddOptions<TemenosAdapterOptions>()
            .Bind(config.GetSection(TemenosAdapterOptions.SectionName))
            .ValidateOnStart();

        // Validated at start-up rather than on the first call: a negative renewal margin or a zero
        // timeout would otherwise surface as a failed customer creation in a Hangfire job, hours
        // after the deployment that caused it.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TemenosAdapterOptions>, TemenosAdapterOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);

        // Singleton, because a token cached per scope is a token minted per call: the dispatcher
        // creates a scope per command, and a cold cache in each would mean one round trip to the
        // authorisation server for every write. It holds no scoped service — see the class
        // remarks on TemenosTokenCache for why minting is passed in as a delegate.
        services.TryAddSingleton<TemenosTokenCache>();

        services.AddScoped<TemenosAuthenticator>();
        services.AddScoped<TemenosTransport>();
        services.AddScoped<TemenosCodeTranslation>();

        services.AddHttpClient(TemenosTransport.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                // No BaseAddress: one pooled client serves every tenant's connection and each has
                // its own base URL, so every request is built with an absolute URI instead.
                //
                // No per-tenant header either, for the same reason and a sharper one: a bearer
                // token on DefaultRequestHeaders here is how one tenant calls the other's
                // installation with the other's credentials. The token is set on the request
                // message, per call, in TemenosTransport.
                var options = sp.GetRequiredService<IOptions<TemenosAdapterOptions>>().Value;

                // Infinite, deliberately. The per-call budget is a linked CancellationToken in
                // TemenosTransport, which is what lets our own expiry be told apart from the
                // caller giving up — HttpClient.Timeout reports both as the same exception, and
                // the distinction decides whether the result is Transient or rethrown.
                client.Timeout = Timeout.InfiniteTimeSpan;

                // A gateway in front of an unreachable installation answers an HTML page, and
                // reading an unbounded one into memory is how one bad route exhausts the process.
                client.MaxResponseContentBufferSize = options.MaxResponseBytes;
            });

        // NOT wired, deliberately: SsrfSafeHandler refuses every RFC 1918 address, and a Temenos
        // Transact installation is frequently on-premise at 10.x or 192.168.x — the ordinary case
        // this adapter exists to serve. It is opt-in per HttpClient, so the correct action is
        // simply never to call it. The threat it defends against is a URL supplied by a caller; a
        // connection's base URL is set by an administrator of the deployment through INT-03.
        //
        //   .ConfigurePrimaryHttpMessageHandler(() => new SsrfSafeHandler(...))   // do not add

        // Scoped, like every adapter: it resolves the tenant's connection once per scope and
        // holds it, and a singleton would carry one tenant's binding into the next tenant's job.
        services.AddKeyedScoped<ICbsAdapter, TemenosAdapter>(IntegrationKind.Temenos.ToString());

        return services;
    }
}
