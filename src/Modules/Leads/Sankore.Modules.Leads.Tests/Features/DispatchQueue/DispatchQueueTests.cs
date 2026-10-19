namespace Sankore.Modules.Leads.Tests.Features.DispatchQueue;

using FluentAssertions;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Features.DispatchQueue;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A lead no agent could take used to stay unassigned for good, and the failure event announcing
/// it was rolled back with the failed dispatch. It now waits in a queue, is retried under the
/// dispatching rule until an agent is available, and its agency head gets one task meanwhile.
/// </summary>
public sealed class DispatchQueueTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _head = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public DispatchQueueTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    // ── The handler queues, and announces once ──────────────────────────────

    [Fact]
    public async Task A_failed_dispatch_queues_the_lead_and_announces_it_once()
    {
        var lead = await AddLeadAsync();
        var publisher = Substitute.For<IEventPublisher>();

        await DispatchAsync(lead.Id, Pool(), publisher);
        await DispatchAsync(lead.Id, Pool(), publisher);   // the queue's retry, still nobody

        (await ReloadAsync(lead.Id)).AwaitingDispatchSince.Should().NotBeNull();
        await publisher.Received(1).PublishAsync(
            Arg.Is<LeadDispatchingFailedEvent>(e => e.LeadId == lead.Id && e.Reason == "NO_AGENT_AVAILABLE"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_successful_dispatch_takes_the_lead_out_of_the_queue()
    {
        var lead = await AddLeadAsync();
        await DispatchAsync(lead.Id, Pool(), Substitute.For<IEventPublisher>());

        var result = await DispatchAsync(
            lead.Id, Pool(AgentTestBuilder.Create().Named("Awa Fall").Build()), Substitute.For<IEventPublisher>());

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(lead.Id)).AwaitingDispatchSince.Should().BeNull();
    }

    // ── The agency head is told, once, and the task closes itself ───────────

    [Fact]
    public async Task Without_rules_the_agency_head_gets_a_manual_dispatch_task()
    {
        var lead = await AddLeadAsync(withAgency: true);

        await FailedAsync(lead.Id);
        await FailedAsync(lead.Id);   // redelivered

        var task = (await TasksAsync(lead.Id)).Should().ContainSingle().Subject;
        task.Type.Should().Be(CrmTaskType.ManualDispatch);
        task.AssignedAgentId.Should().Be(_head);
        task.Status.Should().Be(CrmTaskStatus.Pending);
    }

    [Fact]
    public async Task A_lead_without_agency_waits_in_the_queue_without_a_task()
    {
        var lead = await AddLeadAsync(withAgency: false);

        await FailedAsync(lead.Id);

        (await TasksAsync(lead.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_assignment_closes_the_manual_dispatch_task()
    {
        var lead = await AddLeadAsync(withAgency: true);
        await FailedAsync(lead.Id);

        var agent = Guid.NewGuid();
        var context = Substitute.For<ConsumeContext<LeadDispatchedEvent>>();
        context.Message.Returns(new LeadDispatchedEvent(
            lead.Id, _tenantId, agent, DispatchingStrategy.RoundRobin, 50, DateTimeOffset.UtcNow.AddHours(4)));
        await using (var db = _db.CreateContext())
            await new LeadDispatchedTaskConsumer(db, NullLogger<LeadDispatchedTaskConsumer>.Instance).Consume(context);

        var tasks = await TasksAsync(lead.Id);
        tasks.Single(t => t.Type == CrmTaskType.ManualDispatch).Status.Should().Be(CrmTaskStatus.Completed);
        tasks.Single(t => t.Type == CrmTaskType.FirstContact).AssignedAgentId.Should().Be(agent);
    }

    // ── The job retries the queue ───────────────────────────────────────────

    [Fact]
    public async Task The_job_retries_queued_leads_once_an_agent_is_available()
    {
        var queued = await AddLeadAsync();
        var notQueued = await AddLeadAsync();
        await DispatchAsync(queued.Id, Pool(), Substitute.For<IEventPublisher>());

        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<DispatchLeadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DispatchLeadResult(Guid.NewGuid(), Guid.NewGuid(), "Awa", 50, DateTimeOffset.UtcNow)));

        await using var db = _db.CreateContext();
        var placed = await DispatchWaitingLeadsJob.RunAsync(
            _tenantId, db, Pool(AgentTestBuilder.Create().Build()), sender, NullLogger.Instance, default);

        placed.Should().Be(1);
        await sender.Received(1).Send(
            Arg.Is<DispatchLeadCommand>(c => c.LeadId == queued.Id && c.Strategy == null), Arg.Any<CancellationToken>());
        await sender.DidNotReceive().Send(
            Arg.Is<DispatchLeadCommand>(c => c.LeadId == notQueued.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_job_does_nothing_while_nobody_is_available()
    {
        var lead = await AddLeadAsync();
        await DispatchAsync(lead.Id, Pool(), Substitute.For<IEventPublisher>());
        var sender = Substitute.For<ISender>();

        await using var db = _db.CreateContext();
        await DispatchWaitingLeadsJob.RunAsync(_tenantId, db, Pool(), sender, NullLogger.Instance, default);

        await sender.DidNotReceiveWithAnyArgs().Send(default(DispatchLeadCommand)!, default);
    }

    // ── The list shows the queue ────────────────────────────────────────────

    [Fact]
    public async Task The_lead_list_filters_on_the_queue_and_says_since_when()
    {
        var queued = await AddLeadAsync();
        await AddLeadAsync();
        await DispatchAsync(queued.Id, Pool(), Substitute.For<IEventPublisher>());

        await using var db = _db.CreateContext();
        var page = await new Sankore.Modules.Leads.Features.ListLeads.ListLeadsHandler(db).Handle(
            new Sankore.Modules.Leads.Features.ListLeads.ListLeadsQuery(AwaitingDispatch: true), default);

        var item = page.Value.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be(queued.Id);
        item.AwaitingDispatchSince.Should().NotBeNull();
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private async Task<Lead> AddLeadAsync(bool withAgency = false)
    {
        var builder = LeadTestBuilder.Create().WithTenant(_tenantId);
        if (withAgency) builder = builder.WithAgency(_agencyId);
        var lead = builder.Build();

        await using var db = _db.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _db.CreateContext();
        return await db.Leads.FirstAsync(l => l.Id == leadId);
    }

    private async Task<List<CrmTask>> TasksAsync(Guid leadId)
    {
        await using var db = _db.CreateContext();
        return await db.CrmTasks.Where(t => t.LeadId == leadId).ToListAsync();
    }

    private IAdministrationModule Pool(params AgentSummary[] agents)
    {
        var admin = Substitute.For<IAdministrationModule>();
        admin.GetAvailableAgentsAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(agents.ToList());
        admin.ResolveAgencyRecipientAsync(_tenantId, _agencyId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_head);
        return admin;
    }

    private async Task FailedAsync(Guid leadId)
    {
        var context = Substitute.For<ConsumeContext<LeadDispatchingFailedEvent>>();
        context.Message.Returns(new LeadDispatchingFailedEvent(leadId, _tenantId, "NO_AGENT_AVAILABLE"));
        context.CancellationToken.Returns(CancellationToken.None);

        await using var db = _db.CreateContext();
        await new LeadDispatchingFailedTaskConsumer(db, Pool(), NullLogger<LeadDispatchingFailedTaskConsumer>.Instance)
            .Consume(context);
    }

    private async Task<Result<DispatchLeadResult>> DispatchAsync(
        Guid leadId, IAdministrationModule admin, IEventPublisher publisher)
    {
        await using var db = _db.CreateContext();
        var factory = new DispatchingStrategyFactory(
            compatibilityScoring: new CompatibilityScoringStrategy(),
            roundRobin: new RoundRobinStrategy(),
            weightedRoundRobin: new WeightedRoundRobinStrategy(),
            stickyAssignment: new StickyAssignmentStrategy(new CompatibilityScoringStrategy()),
            cherryPicking: new CherryPickingStrategy(new CompatibilityScoringStrategy()));

        var handler = new DispatchLeadHandler(
            db, admin, new CompatibilityScorer(), factory, new DispatchingRuleResolver(db),
            new AgentCapacityService(db, null),
            TestClientAdvisorResolver.None(), TestDistributionGuard.AllowAll(),
            publisher, NullLogger<DispatchLeadHandler>.Instance, TimeProvider.System);

        return await handler.Handle(new DispatchLeadCommand(leadId, _tenantId), default);
    }
}
