namespace Sankore.Modules.Integration.Adapters.Fake;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the in-memory double into the keyed registry every other adapter uses (INT-10).
///
/// <para>
/// The gate is the ENVIRONMENT and not a configuration flag. M02's biometry double is selected by
/// <c>Biometry:UseFake</c>, which is right there: answering a plausible OCR reading in production
/// would be visibly wrong and nobody would ship it on purpose. Here the failure mode is silent —
/// a command reaches <c>Succeeded</c>, an <see cref="ExternalId"/> lands in
/// <c>integration_reference</c>, Customer 360 shows an account — and no money ever moved. A flag
/// left on by a copied appsettings would produce a tenant whose CBS and whose CRM disagree about
/// every write for as long as nobody checks the bank.
/// </para>
///
/// <para>
/// So it fails CLOSED, and loudly: outside Development the method throws rather than silently
/// skipping the registration. A skip would leave the connection resolving to
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> at the first command — hours later, in a
/// job log, far from the deployment that caused it.
/// </para>
/// </summary>
public static class FakeAdapterRegistration
{
    /// <summary>
    /// Registers <see cref="FakeAdapter"/> under <c>IntegrationKind.Fake</c>, the key
    /// <c>IntegrationAdapterResolver</c> looks a connection's adapter up by.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Outside Development. The message carries
    /// <see cref="IntegrationErrors.FakeAdapterNotAllowed"/> so the boot failure names the rule
    /// rather than the symptom.
    /// </exception>
    public static IServiceCollection AddFakeAdapter(
        this IServiceCollection services, IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(env);

        if (!env.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{IntegrationErrors.FakeAdapterNotAllowed}: the in-memory integration adapter is " +
                $"selectable in Development only, and this host runs as '{env.EnvironmentName}'. " +
                "It answers successful writes with no back-office at all, so a deployment that " +
                "reached it would report writes that never happened.");
        }

        // Scoped, like every real adapter: the double keeps the state a flow writes (the account
        // it just opened, the policy it just subscribed), and a singleton would carry one
        // developer's request into the next one's.
        services.AddKeyedScoped<ICbsAdapter, FakeAdapter>(IntegrationKind.Fake.ToString());

        return services;
    }
}
