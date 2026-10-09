namespace Sankore.Api.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sankore.Api.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// Which core-banking adapters this host actually offers a tenant.
///
/// <para>
/// <b>The gap this suite closes, stated plainly.</b> An adapter reaches a connection only if two
/// independent things happened: its project is referenced by <c>Sankore.Api.csproj</c>, and its
/// <c>Add…Adapter</c> was called. Perfect Vision (INT-28) shipped with the first and not the
/// second — the assembly was built, 45 of its own tests passed, and every Perfect Vision
/// connection answered <see cref="IntegrationErrors.AdapterNotRegistered"/>, which reads as "this
/// deployment was built without the assembly" and sends whoever hits it to inspect a build that is
/// perfectly correct. Nothing in the repository could notice, because every existing test asked
/// "does the adapter behave?" and none asked "can a connection reach it?".
/// </para>
///
/// <para>
/// <b>Why registrations and not resolutions.</b> The assertions read the
/// <see cref="ServiceDescriptor"/> list rather than building a provider and resolving. Resolving
/// would drag in <c>IntegrationDbContext</c>, the secrets vault and a configuration the host only
/// has at boot — and it would be answering a different question, one each adapter's own
/// registration suite already answers. The claim here is narrower and is exactly the one that
/// failed: the host asked for it.
/// </para>
///
/// <para>
/// <b>Why the table is exhaustive over the enum.</b> Every <see cref="IntegrationKind"/> must
/// appear in <see cref="Expectations"/> with a verdict, so adding a kind fails this suite until
/// someone states whether this host serves it. A silent default in either direction is what
/// produced the original gap.
/// </para>
/// </summary>
public sealed class IntegrationAdapterCompositionTests
{
    /// <summary>
    /// One verdict per kind, with the reason. <c>true</c> means a tenant on this deployment can
    /// reach an adapter for it — including the four that refuse every call today, because a
    /// refusal naming a missing vendor document is a procurement conversation while an absent
    /// registration is a wild-goose chase through a build.
    /// </summary>
    private static readonly Dictionary<IntegrationKind, bool> Expectations = new()
    {
        // Implemented against Transact's public Party/Holdings APIs (INT-12, INT-13).
        [IntegrationKind.Temenos] = true,

        // Blocked on SBS's interface contract (INT-31); registered to refuse by name.
        [IntegrationKind.Amplitude] = true,

        // Blocked on the Open SAB catalogue (INT-32); registered to refuse by name.
        [IntegrationKind.Sab] = true,

        // Blocked on the vendor's file layout (INT-28); registered to refuse by name. THE case
        // that motivated this suite.
        [IntegrationKind.PerfectVision] = true,

        // The insurance family's adapter (ASS-06) has no project at all yet — blocked on the ORASS
        // specification, lot L8. An Orass connection is configurable and answers
        // AdapterNotRegistered, which is here the honest answer rather than a wiring mistake: no
        // assembly exists to register. Flip this to true the day one does.
        [IntegrationKind.Orass] = false,

        // The in-memory double (INT-10). Development only — asserted separately, in both
        // directions, because this entry describes a NON-Development host.
        [IntegrationKind.Fake] = false,
    };

    [Fact]
    public void Every_kind_has_a_stated_verdict()
    {
        // The guard on the table itself. Without it, a new kind would simply be absent from the
        // theory below and no case would run for it — the test suite would grow quieter as the
        // enum grew, which is the worst possible failure mode for a completeness check.
        Expectations.Keys.Should().BeEquivalentTo(
            Enum.GetValues<IntegrationKind>(),
            "a new IntegrationKind must be declared served or not served by this host");
    }

    public static TheoryData<IntegrationKind, bool> EveryKind()
    {
        var data = new TheoryData<IntegrationKind, bool>();
        foreach (var (kind, expected) in Expectations) data.Add(kind, expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void A_production_host_registers_exactly_the_adapters_it_claims(
        IntegrationKind kind, bool expected)
    {
        var services = Compose("Production");

        IsRegistered(services, kind).Should().Be(
            expected,
            expected
                ? $"a {kind} connection must be able to reach its adapter, even one that refuses"
                : $"{kind} has no adapter on this host; see the reason in Expectations");
    }

    [Fact]
    public void The_in_memory_double_is_offered_in_development_and_nowhere_else()
    {
        // Both directions in one case, because each alone is half the guarantee: present-only
        // would pass on a gate that is always open, absent-only on a registration that never
        // happens. An adapter answering successful writes with no back-office behind it would
        // leave a tenant whose CBS and whose CRM disagree about every write, with nothing
        // reporting it — so the closed direction is the one that matters, and it is also the one
        // a refactoring silently loses.
        IsRegistered(Compose("Development"), IntegrationKind.Fake).Should().BeTrue();

        foreach (var environment in new[] { "Production", "Staging", "Test" })
        {
            IsRegistered(Compose(environment), IntegrationKind.Fake).Should().BeFalse(
                $"the in-memory double must not be offered in {environment}");
        }
    }

    [Fact]
    public void Each_adapter_is_registered_once_and_under_its_own_key()
    {
        var services = Compose("Development");

        var keyed = services
            .Where(d => d.ServiceType == typeof(ICbsAdapter))
            .ToList();

        // One descriptor per key. Two registrations under one key is not a harmless duplicate:
        // GetKeyedService returns the LAST one, so a copy-pasted line would silently decide which
        // adapter serves a vendor — and the resolver would answer a different implementation than
        // the one a reader of this file expects.
        keyed.Select(d => d.ServiceKey).Should().OnlyHaveUniqueItems();

        // Implementation types distinct too, which catches the other half of a copy-paste: two
        // keys pointing at the same adapter class.
        //
        // KeyedImplementationType and not ImplementationType: on a keyed descriptor the latter is
        // null, so the obvious spelling compares five nulls and passes whatever is registered.
        // This test found that on itself.
        keyed.Select(d => d.KeyedImplementationType).Should().OnlyHaveUniqueItems();

        // And every one is a type registration, which is what makes the line above meaningful: a
        // factory descriptor carries no type, so a later switch to AddKeyedScoped(key, factory)
        // would quietly empty the check rather than fail it.
        keyed.Should().OnlyContain(d => d.KeyedImplementationType != null);

        // Scoped, not singleton: an adapter caches the tenant's settings for the scope — including
        // its capability matrix — and a singleton would hand one tenant's matrix to the next
        // tenant's background job.
        keyed.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void A_null_argument_fails_at_composition_rather_than_at_the_first_command()
    {
        var act = () => new ServiceCollection()
            .AddIntegrationAdapters(new ConfigurationBuilder().Build(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static bool IsRegistered(IServiceCollection services, IntegrationKind kind)
        => services.Any(d => d.ServiceType == typeof(ICbsAdapter)
                             && Equals(d.ServiceKey, kind.ToString()));

    private static IServiceCollection Compose(string environmentName)
        => new ServiceCollection().AddIntegrationAdapters(
            new ConfigurationBuilder().Build(),
            new StubEnvironment(environmentName));

    /// <summary>
    /// The host environment, reduced to the one property the composition reads. A substitute would
    /// do, but a stub keeps the test honest about how little is consulted.
    /// </summary>
    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Sankore.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
