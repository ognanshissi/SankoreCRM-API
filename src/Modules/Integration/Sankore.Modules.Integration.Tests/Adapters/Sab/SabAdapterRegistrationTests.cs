namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Sab;
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
/// wrong key — or not at all — a configured SAB connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/>: "this deployment shipped without the
/// assembly", which sends an administrator to inspect a build. Registered correctly, the same
/// connection answers one of three refusals that each name their own owner. One is a wild-goose
/// chase, the others are an afternoon's work or a procurement conversation — and that difference
/// is the whole deliverable of a blocked chantier.
/// </para>
/// </summary>
public sealed class SabAdapterRegistrationTests
{
    [Fact]
    public void It_registers_the_adapter_under_its_kind()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetKeyedService<ICbsAdapter>(
            IntegrationKind.Sab.ToString());

        // IntegrationAdapterResolver reads connection.Kind.ToString() and nothing else, so a
        // registration under any other name is a registration no connection can reach.
        adapter.Should().NotBeNull().And.BeOfType<SabAdapter>();
    }

    [Fact]
    public void There_is_no_environment_gate_unlike_the_in_memory_double()
    {
        // The double's gate exists because it answers plausible successes with no back-office at
        // all. Nothing here can report a write that did not happen: every method refuses, which is
        // as correct in production as in Development. A gate would mean a production deployment
        // answered ADAPTER_NOT_REGISTERED instead of naming what is actually missing.
        using var provider = Compose();
        using var scope = provider.CreateScope();

        scope.ServiceProvider
            .GetKeyedService<ICbsAdapter>(IntegrationKind.Sab.ToString())
            .Should().NotBeNull();
    }

    [Fact]
    public void The_adapter_is_scoped_to_one_request()
    {
        using var provider = Compose();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Sab.ToString());
        var b = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Sab.ToString());
        var c = second.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Sab.ToString());

        a.Should().BeSameAs(b, "one request sees one adapter, as it would with a real one");

        // It caches the tenant's installation for the scope — the entity it would be scoped to and
        // the connection id its vault key is built from — so a singleton would hand one tenant's
        // institution to the next tenant's Hangfire job. On a multi-IMF network that is the exact
        // mistake criterion 2 exists to prevent, arriving through dependency injection instead of
        // through a wrong setting.
        c.Should().NotBeSameAs(a);
    }

    [Fact]
    public void It_refuses_to_be_wired_without_the_arguments_it_declares()
    {
        var noServices = () =>
            SabAdapterRegistration.AddSabAdapter(null!, new ConfigurationBuilder().Build());

        var noConfig = () => new ServiceCollection().AddSabAdapter(null!);

        // Both at boot, never at the first command. A host that forgets an argument has to learn
        // it from a failed start-up and not from a rejected customer creation in a job log hours
        // later.
        noServices.Should().Throw<ArgumentNullException>();
        noConfig.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void It_does_not_overwrite_a_clock_the_module_already_registered()
    {
        var services = new ServiceCollection();
        var clock = new StoppedClock();

        services.AddSingleton<TimeProvider>(clock);
        services.AddSabAdapter(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        // TryAdd, so the last word on the clock does not depend on the order the host calls the
        // module and its adapters in. A test clock silently replaced by the system one is the kind
        // of thing that only ever shows up as a flaky assertion on a timestamp.
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public void It_registers_no_secrets_vault_of_its_own()
    {
        var services = new ServiceCollection();

        services.AddSabAdapter(new ConfigurationBuilder().Build());

        // The adapter consumes the host's vault (AddSecretsVault) and must not bring a second one:
        // two vaults in one host means two master keys, and the one that did not encrypt a value
        // cannot decrypt it. A connection's API key would read as absent, which is a refusal the
        // administrator cannot clear by saving the key again.
        services.Should().NotContain(d => d.ServiceType == typeof(ISecretsModule));
    }

    /// <summary>
    /// The adapter's own dependencies, as the module and the host would provide them: a store, a
    /// tenant, the vault and a logger. The registration deliberately provides none of those — it
    /// is a consumer of <c>AddIntegrationModule</c> and <c>AddSecretsVault</c>, not a second copy
    /// of either, which is what makes "the host wires it" true rather than aspirational.
    /// </summary>
    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();

        services.AddSabAdapter(new ConfigurationBuilder().Build());

        var factory = new TestIntegrationDbContextFactory(SabHarness.TenantId);

        services.AddScoped(_ => factory.CreateContext());
        services.AddSingleton<ITenantContext>(new FixedTenantContext(SabHarness.TenantId));
        services.AddSingleton(Substitute.For<ISecretsModule>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return services.BuildServiceProvider();
    }

    /// <summary>A clock that is not <see cref="TimeProvider.System"/>, which is all this needs.</summary>
    private sealed class StoppedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
