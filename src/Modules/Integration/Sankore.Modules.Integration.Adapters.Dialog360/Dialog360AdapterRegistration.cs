namespace Sankore.Modules.Integration.Adapters.Dialog360;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the 360dialog adapter into the keyed registry <c>IntegrationAdapterResolver</c> looks a
/// connection's adapter up by (WA-BE-01).
///
/// <para>
/// <b>Keyed on <c>IntegrationKind.Dialog360</c></b>, where the specification names the key
/// <c>whatsapp-360dialog</c>: the resolver keys every adapter of this module on its kind's name,
/// and a second naming scheme for one family would be a registration no connection can reach.
/// </para>
///
/// <para>
/// No environment gate: with no configured connection the resolver answers
/// <c>NoActiveConnection</c>, so shipping the adapter costs nothing to a tenant without WhatsApp.
/// It needs <c>AddIntegrationModule</c> (the call journal) and <c>AddSecretsVault</c> (the
/// per-number API key) to have run, and registers neither.
/// </para>
/// </summary>
public static class Dialog360AdapterRegistration
{
    public static IServiceCollection AddDialog360Adapter(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<Dialog360Transport>();

        services.AddHttpClient(Dialog360Transport.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                // No BaseAddress and no default header: one pooled client serves every number of
                // every tenant, each with its own key, set per request in Dialog360Transport.
                //
                // Infinite here because the per-attempt budget is a linked token in the transport,
                // which is what tells our own expiry apart from the caller giving up.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.MaxResponseContentBufferSize = Dialog360Transport.MaxResponseBytes;
            });

        // Scoped like every adapter: it holds nothing between calls, but the transport's journal
        // and the vault are scoped services.
        services.AddKeyedScoped<ICbsAdapter, Dialog360Adapter>(IntegrationKind.Dialog360.ToString());

        return services;
    }
}
