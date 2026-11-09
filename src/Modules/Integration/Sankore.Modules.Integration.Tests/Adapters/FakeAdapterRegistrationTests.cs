namespace Sankore.Modules.Integration.Tests.Adapters;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10, acceptance criterion 3 — the fake is registered; who reaches it is FakeAdapterGate's call.
///
/// <para>
/// The gate is asserted rather than documented because its failure mode is silent and permanent.
/// A fake reached in production answers every write with a success and an invented
/// <see cref="ExternalId"/>: the command reaches <c>Succeeded</c>, the reference lands in
/// <c>integration_reference</c>, Customer 360 shows the account — and no money ever moved. The
/// tenant's CBS and its CRM would then disagree about every write for as long as nobody thinks to
/// ask the bank. Nothing downstream can detect that, so the only place it can be stopped is here.
/// </para>
/// </summary>
public sealed class FakeAdapterRegistrationTests
{
    [Fact]
    public void Development_should_register_the_double_under_its_kind()
    {
        var services = new ServiceCollection();

        services.AddFakeAdapter(StubHostEnvironment.Development);

        using var provider = services.BuildServiceProvider();
        var adapter = provider.GetKeyedService<ICbsAdapter>(IntegrationKind.Fake.ToString());

        // The key is what IntegrationAdapterResolver looks a connection's adapter up by — it reads
        // connection.Kind.ToString() and nothing else, so a registration under any other name is
        // a registration no connection can reach.
        adapter.Should().NotBeNull().And.BeOfType<FakeAdapter>();
    }

    [Fact]
    public void Every_platform_registers_it_and_the_gate_decides_who_reaches_it()
    {
        // Since 2026-11-08 (owner): a demo on the production platform subscribes through the
        // double. Reaching it is FakeAdapterGate's decision — Development or a test institution —
        // pinned in FakeAdapterGateTests.
        foreach (var env in new[] { StubHostEnvironment.Production, StubHostEnvironment.Staging })
        {
            var services = new ServiceCollection();
            services.AddFakeAdapter(env);
            using var provider = services.BuildServiceProvider();
            provider.GetKeyedService<ICbsAdapter>(IntegrationKind.Fake.ToString()).Should().NotBeNull();
        }
    }

    [Fact]
    public void The_registered_double_should_be_scoped_to_one_request()
    {
        var services = new ServiceCollection();
        services.AddFakeAdapter(StubHostEnvironment.Development);

        using var provider = services.BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var a = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Fake.ToString());
        var b = first.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Fake.ToString());
        var c = second.ServiceProvider.GetRequiredKeyedService<ICbsAdapter>(
            IntegrationKind.Fake.ToString());

        a.Should().BeSameAs(b, "one request sees one adapter, as it would with a real one");

        // A singleton would carry the accounts one developer opened into the next request — and,
        // worse, into another tenant's flow in a shared dev environment.
        c.Should().NotBeSameAs(a);
    }
}
