namespace Sankore.Modules.Integration.Adapters.Fake;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wires the in-memory double into the keyed registry every other adapter uses (INT-10).
///
/// <para>
/// The double answers every write with a success and an invented <see cref="ExternalId"/>: a REAL
/// institution reaching it would believe accounts were opened and policies subscribed while no back
/// office ever heard of them. It was Development-only for that reason (INT-10). Since 2026-11-08
/// (owner, <c>docs/donnees-demarrage-demo.md</c>) it is registered on every platform so demos on the
/// production platform can use it, and the rule moved to where a connection is used:
/// <c>FakeAdapterGate</c> lets only Development or a TEST institution create, activate or resolve a
/// connection of this kind — checked at every resolution, so an institution whose test flag is
/// lifted loses it at its next call.
/// </para>
/// </summary>
public static class FakeAdapterRegistration
{
    /// <summary>
    /// Registers <see cref="FakeAdapter"/> under <c>IntegrationKind.Fake</c>, the key
    /// <c>IntegrationAdapterResolver</c> looks a connection's adapter up by.
    /// </summary>
    public static IServiceCollection AddFakeAdapter(
        this IServiceCollection services, IHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(env);

        // Registered on every platform since 2026-11-08 (owner, docs/donnees-demarrage-demo.md): a
        // demo on the production platform subscribes through it. Who may REACH it is decided at
        // each use by FakeAdapterGate — Development, or a test institution — never by its presence.

        // Scoped, like every real adapter: the double keeps the state a flow writes (the account
        // it just opened, the policy it just subscribed), and a singleton would carry one
        // developer's request into the next one's.
        //
        // Scoped means a fresh sequence per request, so every policy, customer or debit the double
        // minted was « …-0001 » — and the second subscription of a tenant was refused as a
        // duplicate of the first (INTEGRATION_DUPLICATE on POL-0001). The sequence's starting
        // point therefore comes from a singleton, which gives each scope its own range. Still a
        // TYPE registration: IntegrationAdapterCompositionTests requires one, a factory would
        // empty its duplicate checks.
        services.AddSingleton<FakeAdapterSequence>();
        services.AddKeyedScoped<ICbsAdapter, FakeAdapter>(IntegrationKind.Fake.ToString());

        return services;
    }
}

/// <summary>
/// Where each development scope of <see cref="FakeAdapter"/> starts numbering. From the clock, so a
/// restart does not reuse the references already in the database, and a step per scope, so two
/// requests of one process never meet.
/// </summary>
public sealed class FakeAdapterSequence
{
    private int _next = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100_000_000) * 10;

    public int NextStart() => Interlocked.Add(ref _next, 1_000) - 1_000;
}
