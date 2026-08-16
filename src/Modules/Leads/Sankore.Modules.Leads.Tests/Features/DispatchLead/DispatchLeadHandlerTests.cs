using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Leads.Tests.Features.DispatchLead;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Users.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// These tests exercise DispatchLeadHandler against a real EF Core
/// InMemory-provider LeadsDbContext (so multi-tenant query filters and
/// EF mapping quirks are genuinely exercised) while faking the
/// cross-module IUsersModule and the IEventPublisher — exactly the two
/// seams the slice was designed around.
///
/// For a fully realistic run against actual PostgreSQL semantics (array
/// columns, owned types, concurrency tokens), promote these to
/// Testcontainers-based integration tests as described in the user story
/// (Sankore.IntegrationTests project).
/// </summary>
public sealed class DispatchLeadHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public DispatchLeadHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_assign_lead_to_the_most_compatible_agent()
    {
        // ARRANGE
        await using var db = _factory.CreateContext();

        var lead = LeadTestBuilder.Create()
            .WithTenant(_tenantId)
            .WithLanguage("Wolof")
            .WithProduct("Crédit individuel")
            .WithLocation(14.6928, -17.4467)
            .Build();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var awa = AgentTestBuilder.Create()
            .Named("Awa Fall")
            .SpeakingLanguages("Wolof", "FR")
            .SpecializedIn("Crédit individuel")
            .LocatedAt(14.70, -17.45)
            .WithLoad(12)
            .WithConversionRate(0.6)
            .Build();

        var ibra = AgentTestBuilder.Create()
            .Named("Ibra Ba")
            .SpeakingLanguages("FR")
            .SpecializedIn("Crédit individuel")
            .LocatedAt(14.71, -17.46)
            .WithLoad(8)
            .WithConversionRate(0.55)
            .Build();

        var usersModule = Substitute.For<IUsersModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { awa, ibra });

        var publisher = Substitute.For<IEventPublisher>();

        var handler = BuildHandler(db, usersModule, publisher);

        // ACT
        var result = await handler.Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        // ASSERT
        result.IsSuccess.Should().BeTrue();
        result.Value.AgentName.Should().Be("Awa Fall"); // language match tips the balance
        result.Value.CompatibilityScore.Should().BeGreaterThan(70);

        var storedLead = await db.Leads.FindAsync(lead.Id);
        storedLead!.Status.Should().Be(LeadStatus.Assigned);
        storedLead.CurrentAssignmentId.Should().NotBeNull();

        await publisher.Received(1).PublishAsync(
            Arg.Is<Sankore.Modules.Leads.Features.DispatchLead.Events.LeadDispatchedEvent>(
                e => e.LeadId == lead.Id && e.AgentId == awa.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_fail_when_no_agent_is_available()
    {
        await using var db = _factory.CreateContext();

        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var usersModule = Substitute.For<IUsersModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary>());

        var publisher = Substitute.For<IEventPublisher>();
        var handler = BuildHandler(db, usersModule, publisher);

        var result = await handler.Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NO_AGENT_AVAILABLE");

        await publisher.Received(1).PublishAsync(
            Arg.Any<Sankore.Modules.Leads.Features.DispatchLead.Events.LeadDispatchingFailedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_reject_dispatching_a_lead_that_is_not_sales_qualified()
    {
        await using var db = _factory.CreateContext();

        // Build a lead but do NOT qualify it (stays in "New" status).
        var lead = Lead.Capture(
            tenantId: _tenantId,
            fullName: "Fresh Prospect",
            phoneNumber: "+221770000000",
            source: LeadSource.Web,
            interestedProduct: "Épargne",
            preferredLanguage: "FR",
            location: new GeoPoint(14.69, -17.44),
            preferredAgencyId: null,
            clock: TimeProvider.System);

        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var usersModule = Substitute.For<IUsersModule>();
        var publisher = Substitute.For<IEventPublisher>();
        var handler = BuildHandler(db, usersModule, publisher);

        var result = await handler.Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_NOT_QUALIFIED");
        await usersModule.DidNotReceive().GetAvailableAgentsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_block_dispatching_when_anti_monopoly_threshold_is_reached()
    {
        await using var db = _factory.CreateContext();

        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        // Single candidate, already at (or above) the default anti-monopoly threshold of 5 hot leads.
        var overloadedAgent = AgentTestBuilder.Create()
            .Named("Overloaded Agent")
            .WithHotLeads(5)
            .Build();

        var usersModule = Substitute.For<IUsersModule>();
        usersModule.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSummary> { overloadedAgent });

        var publisher = Substitute.For<IEventPublisher>();
        var handler = BuildHandler(db, usersModule, publisher);

        var result = await handler.Handle(
            new DispatchLeadCommand(lead.Id, _tenantId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("ANTI_MONOPOLY_BLOCKED");

        await publisher.Received(1).PublishAsync(
            Arg.Any<Sankore.Modules.Leads.Features.DispatchLead.Events.AntiMonopolyTriggeredEvent>(),
            Arg.Any<CancellationToken>());
    }

    private static DispatchLeadHandler BuildHandler(
        Sankore.Modules.Leads.Infrastructure.LeadsDbContext db,
        IUsersModule usersModule,
        IEventPublisher publisher)
    {
        // NOTE: [FromKeyedServices] on the handler's constructor parameter
        // only matters when the DI container resolves the handler (as
        // MediatR does at runtime). Constructing the handler directly here,
        // as any unit test would, simply passes the fake publisher — the
        // keying is irrelevant outside of container resolution.
        var scorer = new CompatibilityScorer();
        var factory = new DispatchingStrategyFactory(
            compatibilityScoring: new CompatibilityScoringStrategy(),
            roundRobin: new RoundRobinStrategy(),
            weightedRoundRobin: new WeightedRoundRobinStrategy(),
            stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
            cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy()));

        return new DispatchLeadHandler(
            db, usersModule, scorer, factory, publisher,
            NullLogger<DispatchLeadHandler>.Instance, TimeProvider.System);
    }
}
