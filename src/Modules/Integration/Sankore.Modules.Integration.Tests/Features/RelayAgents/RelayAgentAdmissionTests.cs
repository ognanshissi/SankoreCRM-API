namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using FluentAssertions;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The authoritative check criterion 2 rests on. Tested as a unit because the channel that will
/// call it does not exist yet: what it answers, for each state an agent can be in, is the whole of
/// the contract that channel's author will read.
/// </summary>
public sealed class RelayAgentAdmissionTests
{
    private const string Thumbprint =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private static IRelayAgentAdmission Admission(IntegrationDbContext db)
        => new RelayAgentAdmission(db, RelayAgentsTestHarness.Log<RelayAgentAdmission>());

    [Fact]
    public async Task An_active_agent_is_admitted_and_its_tenant_comes_back()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint, name: "Relais Korhogo");

        await using var db = factory.CreateContext();
        var identity = await Admission(db).AdmitAsync(Thumbprint, default);

        identity.Should().NotBeNull();
        identity!.AgentId.Should().Be(agent.Id);
        identity.Name.Should().Be("Relais Korhogo");

        // The tenant is what the dispatcher compares against connection.TenantId — §5ter's second
        // obligation, which lives in the dispatcher and not here, and which is impossible unless
        // this method hands the tenant back.
        identity.TenantId.Should().Be(RelayAgentsTestHarness.Tenant);
    }

    [Fact]
    public async Task It_resolves_an_agent_of_a_tenant_the_context_is_not_bound_to()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.OtherTenant, Thumbprint);

        // Stands in for production, where an agent channel has no tenant context at all: without
        // IgnoreQueryFilters the global filter would compare TenantId against the ambient value
        // and EVERY agent would be refused, with nothing in the logs to say why.
        await using var db = factory.CreateContext();
        var identity = await Admission(db).AdmitAsync(Thumbprint, default);

        identity.Should().NotBeNull();
        identity!.AgentId.Should().Be(agent.Id);
        identity.TenantId.Should().Be(RelayAgentsTestHarness.OtherTenant);
    }

    [Fact]
    public async Task A_pending_agent_is_not_admitted()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant);

        await using var db = factory.CreateContext();

        // No certificate is pinned yet, so there is nothing to admit — and in particular a
        // Pending agent's null thumbprint must not match a null-ish input.
        (await Admission(db).AdmitAsync(Thumbprint, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_thumbprint_is_refused_without_matching_cleared_rows(string? value)
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        // A Pending agent and a Revoked one both have a NULL thumbprint in the table. If a blank
        // input reached the query it would be the shape that matches them, and a caller sending no
        // certificate at all would be admitted as whichever row came first.
        RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant, name: "Pending");

        await using var db = factory.CreateContext();
        (await Admission(db).AdmitAsync(value, default)).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_certificate_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();
        var other = new string('e', 64);

        (await Admission(db).AdmitAsync(other, default)).Should().BeNull();
    }

    [Fact]
    public async Task The_comparison_does_not_depend_on_the_case_the_caller_sends()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();

        // Both sides are normalised to lower case because PostgreSQL string equality is
        // case-sensitive: an agent whose tooling prints an upper-case fingerprint would otherwise
        // be refused for ever, and the symptom would be indistinguishable from a revocation.
        (await Admission(db).AdmitAsync($"  {Thumbprint.ToUpperInvariant()}  ", default))
            .Should().NotBeNull();
    }
}
