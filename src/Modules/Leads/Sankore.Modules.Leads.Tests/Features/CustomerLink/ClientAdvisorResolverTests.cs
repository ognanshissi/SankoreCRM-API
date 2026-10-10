namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the "client history" dispatching criterion (F13.10 / BNF-B-22): a request from a known
/// customer goes to that customer's own advisor, when that advisor is available.
/// </summary>
public sealed class ClientAdvisorResolverTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _advisor = Guid.NewGuid();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    private ClientAdvisorResolver Resolver() =>
        new(_customers, NullLogger<ClientAdvisorResolver>.Instance);

    private Lead Lead(Guid? customerId) => Sankore.Modules.Leads.Domain.Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Agency,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        customerId: customerId,
        customerLinkIsAutomatic: true);

    private static AgentSummary Agent(Guid id) => new(
        Id: id, FullName: "Agent Test", AgencyId: Guid.NewGuid(),
        SpokenLanguages: [], Specialties: [], CurrentLocation: null,
        ActiveLeadsCount: 0, HotLeadsCount: 0, ConversionRate30d: 0, IsAvailable: true);

    private void ClientHasAdvisor(Guid? advisorUserId) =>
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(
                _customer, "CLI-0001", "Individual", "Aya Kouassi", "Active",
                Guid.NewGuid(), advisorUserId, "Approved", "Low", null));

    [Fact]
    public async Task Routes_to_the_clients_advisor_when_they_are_in_the_eligible_pool()
    {
        ClientHasAdvisor(_advisor);

        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [Agent(_advisor), Agent(Guid.NewGuid())], CancellationToken.None);

        resolved!.Id.Should().Be(_advisor);
    }

    [Fact]
    public async Task Falls_through_when_the_advisor_is_not_available()
    {
        // "si ce conseiller est disponible" is a condition, not a preference — routing to an
        // unavailable advisor would park the lead with somebody who is not working it.
        ClientHasAdvisor(_advisor);

        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [Agent(Guid.NewGuid())], CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Falls_through_when_the_client_has_no_advisor()
    {
        ClientHasAdvisor(null);

        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [Agent(_advisor)], CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_new_prospect_never_reaches_M01()
    {
        // The overwhelming majority of leads. One avoided cross-module call per dispatch.
        var resolved = await Resolver().ResolveAsync(
            Lead(customerId: null), [Agent(_advisor)], CancellationToken.None);

        resolved.Should().BeNull();
        await _customers.DidNotReceive().GetClientSummaryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_pool_never_reaches_M01()
    {
        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [], CancellationToken.None);

        resolved.Should().BeNull();
        await _customers.DidNotReceive().GetClientSummaryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_M01_outage_lets_the_configured_strategy_run()
    {
        // Routing must not stop because M01 is unreachable: the lead still reaches an agent, just
        // not necessarily the client's own.
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns<Task<ClientSummary?>>(_ => throw new HttpRequestException("M01 unreachable"));

        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [Agent(_advisor)], CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_client_falls_through()
    {
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns((ClientSummary?)null);

        var resolved = await Resolver().ResolveAsync(
            Lead(_customer), [Agent(_advisor)], CancellationToken.None);

        resolved.Should().BeNull();
    }
}
