namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Compliance;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// CIMA article 503 applied by the dispatcher: an insurance lead is not routed to an agent who
/// holds no professional card.
///
/// <para>
/// The stage sits after the administrator's exclusion list and before every heuristic, and these
/// cases pin why: an unlicensed agent is not a worse candidate to be ranked down, they are not a
/// candidate at all. A dispatch that put a lead on them would park it on somebody who cannot
/// lawfully work it while the SLA clock runs.
/// </para>
/// </summary>
public sealed class DispatchLicenceGuardTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public DispatchLicenceGuardTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedInsuranceLeadAsync()
    {
        await using var db = _factory.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenantId,
            fullName: "Awa Ouattara",
            phoneNumber: "+2250700000001",
            source: LeadSource.Agency,
            interestedProduct: "Assurance santé",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System);

        // A resolved product line is what makes the guard ask anything at all. The guard's double
        // is what decides whether this line is an insurance one.
        lead.SetProductLine(Guid.NewGuid()).IsSuccess.Should().BeTrue();

        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private DispatchLeadHandler Handler(
        LeadsDbContext db,
        IAdministrationModule users,
        IEventPublisher publisher,
        InsuranceDistributionGuard guard)
        => new(
            db, users, new CompatibilityScorer(),
            new DispatchingStrategyFactory(
                compatibilityScoring: new CompatibilityScoringStrategy(),
                roundRobin: new RoundRobinStrategy(),
                weightedRoundRobin: new WeightedRoundRobinStrategy(),
                stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
                cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy())),
            new DispatchingRuleResolver(db),
            new AgentCapacityService(db, null),
            TestClientAdvisorResolver.None(),
            guard,
            publisher,
            NullLogger<DispatchLeadHandler>.Instance,
            TimeProvider.System);

    private static IAdministrationModule UsersWith(params AgentSummary[] agents)
    {
        var module = Substitute.For<IAdministrationModule>();
        module.GetAvailableAgentsAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(agents.ToList());
        return module;
    }

    [Fact]
    public async Task An_insurance_lead_is_not_dispatched_to_an_agent_with_no_card()
    {
        var lead = await SeedInsuranceLeadAsync();

        await using var db = _factory.CreateContext();
        var publisher = Substitute.For<IEventPublisher>();

        var handler = Handler(
            db,
            UsersWith(AgentTestBuilder.Create().Named("Sans carte").Build()),
            publisher,
            TestDistributionGuard.InsuranceWithoutLicence());

        var result = await handler.Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        // Its own reason code, not NO_AGENT_AVAILABLE: an insurance lead whose whole agency holds
        // no card is not a capacity problem, and saying it is would send an operator to the
        // staffing screen instead of the credential register.
        result.Error.Should().Be("NO_LICENSED_AGENT_AVAILABLE");
    }

    [Fact]
    public async Task And_the_failure_is_announced_as_a_licence_failure()
    {
        var lead = await SeedInsuranceLeadAsync();

        await using var db = _factory.CreateContext();
        var publisher = Substitute.For<IEventPublisher>();

        await Handler(
                db,
                UsersWith(AgentTestBuilder.Create().Build()),
                publisher,
                TestDistributionGuard.InsuranceWithoutLicence())
            .Handle(new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            Arg.Is<LeadDispatchingFailedEvent>(e => e.Reason == "NO_LICENSED_AGENT_AVAILABLE"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_insurance_lead_IS_dispatched_to_an_agent_who_holds_one()
    {
        // The counterpart, and it is not decoration: without it the case above would pass just as
        // well against a dispatcher that refused every insurance lead.
        var lead = await SeedInsuranceLeadAsync();

        await using var db = _factory.CreateContext();

        var result = await Handler(
                db,
                UsersWith(AgentTestBuilder.Create().Named("Avec carte").Build()),
                Substitute.For<IEventPublisher>(),
                TestDistributionGuard.InsuranceWithLicence())
            .Handle(new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_insurance_lead_is_dispatched_to_an_agent_with_no_card()
    {
        // The licence governs insurance. Gating every dispatch on it would make a professional
        // card a precondition for handing somebody a savings enquiry.
        var lead = await SeedInsuranceLeadAsync();

        await using var db = _factory.CreateContext();

        var result = await Handler(
                db,
                UsersWith(AgentTestBuilder.Create().Build()),
                Substitute.For<IEventPublisher>(),
                // Not an insurance line, and no licence anywhere.
                TestDistributionGuard.Build(insurance: false, licensed: false))
            .Handle(new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_unlicensed_agents_leave_the_pool()
    {
        // The filter narrows; it does not empty. With one licensed agent among several the
        // dispatch must still succeed, on that agent.
        var lead = await SeedInsuranceLeadAsync();

        var licensed = AgentTestBuilder.Create().Named("Avec carte").Build();
        var unlicensed = AgentTestBuilder.Create().Named("Sans carte").Build();

        var provider = Substitute.For<Sankore.Shared.Kernel.Compliance.IDistributionComplianceProvider>();
        provider.IsInsuranceProductLineAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        provider.GetDistributionEligibilitiesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Sankore.Shared.Kernel.Compliance.DistributionEligibilitySnapshot>
            {
                [licensed.Id] = new(
                    Sankore.Shared.Kernel.Compliance.DistributionEligibility.Valid,
                    new DateOnly(2030, 1, 1), true, 48),
                [unlicensed.Id] =
                    Sankore.Shared.Kernel.Compliance.DistributionEligibilitySnapshot.NotRecorded,
            });

        await using var db = _factory.CreateContext();

        var result = await Handler(
                db,
                UsersWith(licensed, unlicensed),
                Substitute.For<IEventPublisher>(),
                new InsuranceDistributionGuard(
                    provider, TimeProvider.System,
                    NullLogger<InsuranceDistributionGuard>.Instance))
            .Handle(new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AgentId.Should().Be(licensed.Id);
    }
}
