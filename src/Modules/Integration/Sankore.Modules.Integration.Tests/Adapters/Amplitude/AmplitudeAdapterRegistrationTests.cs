namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The adapter is reachable, and reachable under the one key that matters.
///
/// <para>
/// Asserted rather than assumed because the two failures read nothing alike. Registered under the
/// wrong key — or not at all — a configured Amplitude connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/>: "this deployment shipped without the
/// assembly", which sends an administrator to inspect a build. Registered correctly, the same
/// connection answers <see cref="IntegrationErrors.AdapterSpecificationPending"/> naming the
/// artefact its release needs from SBS. One is a wild-goose chase, the other is a procurement
/// conversation — and that difference is the whole deliverable of a blocked chantier.
/// </para>
/// </summary>
public sealed class AmplitudeAdapterRegistrationTests
{
    [Fact]
    public void It_registers_the_adapter_under_its_kind()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetKeyedService<ICbsAdapter>(
            IntegrationKind.Amplitude.ToString());

        // IntegrationAdapterResolver reads connection.Kind.ToString() and nothing else, so a
        // registration under any other name is a registration no connection can reach.
        adapter.Should().NotBeNull().And.BeOfType<AmplitudeAdapter>();
    }

    [Fact]
    public void There_is_no_environment_gate_unlike_the_in_memory_double()
    {
        // The double's gate exists because it answers plausible successes with no back-office at
        // all. Nothing here can report a write that did not happen: every method refuses, which is
        // as correct in production as in Development. A gate would mean a production deployment
        // answered ADAPTER_NOT_REGISTERED instead of naming the missing contract.
        using var provider = Compose();
        using var scope = provider.CreateScope();

        scope.ServiceProvider
            .GetKeyedService<ICbsAdapter>(IntegrationKind.Amplitude.ToString())
            .Should().NotBeNull();
    }

    [Fact]
    public void The_adapter_is_scoped_to_one_request()
    {
        using var provider = Compose();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Amplitude.ToString());
        var b = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Amplitude.ToString());
        var c = second.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Amplitude.ToString());

        a.Should().BeSameAs(b, "one request sees one adapter, as it would with a real one");

        // It caches the tenant's release AND its connection mode for the scope, so a singleton
        // would hand one tenant's capability matrix — including whether its installation answers
        // reads live — to the next tenant's Hangfire job.
        c.Should().NotBeSameAs(a);
    }

    [Fact]
    public void It_refuses_to_be_wired_without_the_arguments_it_declares()
    {
        var noServices = () =>
            AmplitudeAdapterRegistration.AddAmplitudeAdapter(
                null!, new ConfigurationBuilder().Build());

        var noConfig = () => new ServiceCollection().AddAmplitudeAdapter(null!);

        // Both at boot, never at the first command. A host that forgets an argument has to learn it
        // from a failed start-up and not from a rejected customer creation in a job log hours later.
        noServices.Should().Throw<ArgumentNullException>();
        noConfig.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void It_does_not_overwrite_a_clock_the_module_already_registered()
    {
        var services = new ServiceCollection();
        var clock = new StoppedClock();

        services.AddSingleton<TimeProvider>(clock);
        services.AddAmplitudeAdapter(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        // TryAdd, so the last word on the clock does not depend on the order the host calls the
        // module and its adapters in. A test clock silently replaced by the system one is the kind
        // of thing that only ever shows up as a flaky assertion on a timestamp.
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    /// <summary>
    /// The adapter's own dependencies, as the module would provide them: a store, a tenant and a
    /// logger. The registration deliberately provides none of those — it is a consumer of
    /// <c>AddIntegrationModule</c> and not a second copy of it, which is what makes "the host wires
    /// it" true rather than aspirational.
    /// </summary>
    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();

        services.AddAmplitudeAdapter(new ConfigurationBuilder().Build());

        var factory = new TestIntegrationDbContextFactory(AmplitudeHarness.TenantId);

        services.AddScoped(_ => factory.CreateContext());
        services.AddSingleton<ITenantContext>(new FixedTenantContext(AmplitudeHarness.TenantId));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return services.BuildServiceProvider();
    }

    /// <summary>A clock that is not <see cref="TimeProvider.System"/>, which is all this needs.</summary>
    private sealed class StoppedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
