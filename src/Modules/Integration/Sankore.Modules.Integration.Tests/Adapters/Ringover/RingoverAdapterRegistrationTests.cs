namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The adapter is reachable, reachable under the one key that matters, and <b>resolvable as
/// <c>ITelephonyPort</c></b>.
///
/// <para>
/// Asserted rather than assumed because the two failures read nothing alike. Registered under the
/// wrong key — or not at all — a configured Ringover connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/>: "this deployment shipped without the
/// assembly", which sends an administrator to inspect a build that is in fact correct. Registered
/// correctly, the same connection answers one of four refusals that each name their own owner.
/// </para>
/// </summary>
public sealed class RingoverAdapterRegistrationTests
{
    [Fact]
    public void It_registers_the_adapter_under_its_kind()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetKeyedService<ICbsAdapter>(
            IntegrationKind.Ringover.ToString());

        // IntegrationAdapterResolver reads connection.Kind.ToString() and nothing else, so a
        // registration under any other name is a registration no connection can reach.
        adapter.Should().NotBeNull().And.BeOfType<RingoverAdapter>();
    }

    [Fact]
    public void The_registered_adapter_is_castable_to_the_telephony_port()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Ringover.ToString());

        // This is the half the key alone does not prove. ResolvePort<ITelephonyPort> casts the
        // keyed ICbsAdapter, so an adapter registered under the right key that did not implement
        // the port would resolve and then fail the cast — at the first click-to-call, in front of
        // an agent, rather than at boot.
        adapter.Should().BeAssignableTo<ITelephonyPort>();
    }

    [Fact]
    public void There_is_no_environment_gate_unlike_the_in_memory_double()
    {
        // The double's gate exists because it answers plausible successes with no provider at all.
        // Nothing here can report a call that did not happen: every operation refuses, which is as
        // correct in production as in Development. A gate would mean a production deployment
        // answered ADAPTER_NOT_REGISTERED instead of naming what is actually missing.
        using var provider = Compose();
        using var scope = provider.CreateScope();

        scope.ServiceProvider
            .GetKeyedService<ICbsAdapter>(IntegrationKind.Ringover.ToString())
            .Should().NotBeNull();
    }

    [Fact]
    public void The_adapter_is_scoped_to_one_request()
    {
        using var provider = Compose();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Ringover.ToString());
        var b = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Ringover.ToString());
        var c = second.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Ringover.ToString());

        a.Should().BeSameAs(b, "one request sees one adapter, as it would with a real one");

        // It caches the tenant's connection for the scope — and therefore the connection id both
        // of its vault keys are built from — so a singleton would hand one tenant's secrets
        // address to the next tenant's Hangfire job.
        c.Should().NotBeSameAs(a);
    }

    [Fact]
    public void It_refuses_to_be_wired_without_the_arguments_it_declares()
    {
        var noServices = () => RingoverAdapterRegistration.AddRingoverAdapter(
            null!, new ConfigurationBuilder().Build());

        var noConfig = () => new ServiceCollection().AddRingoverAdapter(null!);

        // Both at boot, never at the first call. A host that forgets an argument has to learn it
        // from a failed start-up and not from an agent reporting that a button does nothing.
        noServices.Should().Throw<ArgumentNullException>();
        noConfig.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void It_does_not_overwrite_a_clock_the_module_already_registered()
    {
        var services = new ServiceCollection();
        var clock = new StoppedClock();

        services.AddSingleton<TimeProvider>(clock);
        services.AddRingoverAdapter(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        // TryAdd, so the last word on the clock does not depend on the order the host calls the
        // module and its adapters in.
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public void It_registers_no_secrets_vault_of_its_own()
    {
        var services = new ServiceCollection();

        services.AddRingoverAdapter(new ConfigurationBuilder().Build());

        // Two vaults in one host means two master keys, and the one that did not encrypt a value
        // cannot decrypt it. A connection's API key would read as absent, which is a refusal the
        // administrator cannot clear by saving the key again.
        services.Should().NotContain(d => d.ServiceType == typeof(ISecretsModule));
    }

    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();

        services.AddRingoverAdapter(new ConfigurationBuilder().Build());

        var factory = new TestIntegrationDbContextFactory(RingoverHarness.TenantId);

        services.AddScoped(_ => factory.CreateContext());
        services.AddSingleton<ITenantContext>(
            new FixedTenantContext(RingoverHarness.TenantId));
        services.AddSingleton(Substitute.For<ISecretsModule>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return services.BuildServiceProvider();
    }

    /// <summary>A clock that is not <see cref="TimeProvider.System"/>, which is all this needs.</summary>
    private sealed class StoppedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    }
}
