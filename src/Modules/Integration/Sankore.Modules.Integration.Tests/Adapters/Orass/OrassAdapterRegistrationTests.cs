namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The adapter is reachable, and reachable under the one key and the one service type that
/// matter.
///
/// <para>
/// Asserted rather than assumed because the two failures read nothing alike. Registered under the
/// wrong key — or not at all — a configured ORASS connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/>: "this deployment shipped without the
/// assembly", which sends an administrator to inspect a build. Registered correctly, the same
/// connection answers one of five refusals that each name their own owner. One is a wild-goose
/// chase, the others are actionable — and that difference is the whole deliverable of a blocked
/// chantier.
/// </para>
/// </summary>
public sealed class OrassAdapterRegistrationTests
{
    [Fact]
    public void It_registers_the_adapter_under_its_kind()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        var adapter = scope.ServiceProvider.GetKeyedService<ICbsAdapter>(
            IntegrationKind.Orass.ToString());

        // IntegrationAdapterResolver reads connection.Kind.ToString() and nothing else, so a
        // registration under any other name is a registration no connection can reach.
        adapter.Should().NotBeNull().And.BeOfType<OrassAdapter>();
    }

    [Fact]
    public void It_registers_as_the_service_type_the_insurance_path_actually_resolves()
    {
        using var provider = Compose();
        using var scope = provider.CreateScope();

        // The trap an insurance adapter invites: there is no IInsuranceAdapter in this module.
        // IntegrationModuleFacade's ResolveInsurancePort calls
        // IntegrationAdapterResolver.ResolveAdapter, which resolves a keyed ICbsAdapter by kind for
        // BOTH families and only then casts to the insurance port. Registering under an insurance
        // port directly would compile, pass a naive test, and be unreachable in production.
        scope.ServiceProvider
            .GetKeyedService<ICbsAdapter>(IntegrationKind.Orass.ToString())
            .Should().BeAssignableTo<IInsurancePolicyPort>()
            .And.BeAssignableTo<IInsuranceClaimPort>();

        scope.ServiceProvider
            .GetKeyedService<IInsurancePolicyPort>(IntegrationKind.Orass.ToString())
            .Should().BeNull("the resolver never asks for a port by key, so registering one would "
                             + "be a second registration nothing reads");
    }

    [Fact]
    public void There_is_no_environment_gate_unlike_the_in_memory_double()
    {
        // The double's gate exists because it answers plausible successes with no back-office at
        // all — which in this family means reporting a subscription an insurer never accepted.
        // Nothing here can do that: every method refuses, which is as correct in production as in
        // Development. A gate would mean a production deployment answered ADAPTER_NOT_REGISTERED
        // instead of naming what is actually missing.
        using var provider = Compose();
        using var scope = provider.CreateScope();

        scope.ServiceProvider
            .GetKeyedService<ICbsAdapter>(IntegrationKind.Orass.ToString())
            .Should().NotBeNull();
    }

    [Fact]
    public void The_adapter_is_scoped_to_one_request()
    {
        using var provider = Compose();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Orass.ToString());
        var b = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Orass.ToString());
        var c = second.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Orass.ToString());

        a.Should().BeSameAs(b, "one request sees one adapter, as it would with a real one");

        // It caches the tenant's installations for the scope — their branches, their intermediary
        // codes and the connection ids its vault keys are built from — so a singleton would hand
        // one tenant's insurer to the next tenant's Hangfire job. In this family that books one
        // institution's subscription under another's apporteur code, arriving through dependency
        // injection instead of through a wrong setting.
        c.Should().NotBeSameAs(a);
    }

    [Fact]
    public void It_refuses_to_be_wired_without_the_arguments_it_declares()
    {
        var noServices = () =>
            OrassAdapterRegistration.AddOrassAdapter(null!, new ConfigurationBuilder().Build());

        var noConfig = () => new ServiceCollection().AddOrassAdapter(null!);

        // Both at boot, never at the first command. A host that forgets an argument has to learn it
        // from a failed start-up and not from a rejected subscription in a job log hours later.
        noServices.Should().Throw<ArgumentNullException>();
        noConfig.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void It_does_not_overwrite_a_clock_the_module_already_registered()
    {
        var services = new ServiceCollection();
        var clock = new StoppedClock();

        services.AddSingleton<TimeProvider>(clock);
        services.AddOrassAdapter(new ConfigurationBuilder().Build());

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

        services.AddOrassAdapter(new ConfigurationBuilder().Build());

        // The adapter consumes the host's vault (AddSecretsVault) and must not bring a second one:
        // two vaults in one host means two master keys, and the one that did not encrypt a value
        // cannot decrypt it. A connection's credential would read as absent, which is a refusal the
        // administrator cannot clear by saving it again.
        services.Should().NotContain(d => d.ServiceType == typeof(ISecretsModule));
    }

    /// <summary>
    /// The adapter's own dependencies, as the module and the host would provide them: a store, a
    /// tenant, the vault and a logger. The registration deliberately provides none of those — it is
    /// a consumer of <c>AddIntegrationModule</c> and <c>AddSecretsVault</c>, not a second copy of
    /// either, which is what makes "the host wires it" true rather than aspirational.
    /// </summary>
    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();

        services.AddOrassAdapter(new ConfigurationBuilder().Build());

        var factory = new TestIntegrationDbContextFactory(OrassHarness.TenantId);

        services.AddScoped(_ => factory.CreateContext());
        services.AddSingleton<ITenantContext>(new FixedTenantContext(OrassHarness.TenantId));
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
