namespace Sankore.Modules.Leads.Tests.Features.WorkflowEndActions;

using FluentAssertions;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.WorkflowEndActions;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The action of a workflow « Fin » step runs on the lead or the project through the ordinary
/// commands, and what became of it is said back for the circuit's history (2026-11-02).
/// </summary>
public sealed class WorkflowEndActionConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ISender _sender = Substitute.For<ISender>();

    public WorkflowEndActionConsumerTests() => _factory = new TestDbContextFactory(_tenantId);
    public void Dispose() => _factory.Dispose();

    private async Task<WorkflowEndActionOutcome> RunAsync(string action, string entityType, Guid entityId, bool approved = true)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(_sender);
        var consumer = new WorkflowEndActionConsumer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkflowEndActionConsumer>.Instance);

        WorkflowEndActionOutcome? published = null;
        var context = Substitute.For<ConsumeContext<WorkflowEndActionRequested>>();
        context.Message.Returns(new WorkflowEndActionRequested(
            _tenantId, Guid.NewGuid(), Guid.NewGuid(), entityType, entityId, action, approved, "Comité crédit"));
        await context.Publish(Arg.Do<WorkflowEndActionOutcome>(o => published = o), Arg.Any<CancellationToken>());

        await consumer.Consume(context);
        return published!;
    }

    private async Task<Lead> LeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Fresh().Build();
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Opportunity> ProjectAsync()
    {
        var project = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Crédit", "Crédit commerçant", null, TimeProvider.System);
        await using var db = _factory.CreateContext();
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    [Fact]
    public async Task Qualifying_moves_the_lead_to_qualified_and_says_so()
    {
        var lead = await LeadAsync();

        var outcome = await RunAsync(WorkflowEndActions.LeadQualify, "Lead", lead.Id);

        outcome.Done.Should().BeTrue();
        outcome.Message.Should().Be("Lead qualifié par le circuit.");
        await using var db = _factory.CreateContext();
        (await db.Leads.SingleAsync(l => l.Id == lead.Id)).Status.Should().Be(LeadStatus.Qualified);
    }

    [Fact]
    public async Task Losing_a_lead_goes_through_the_close_command_with_the_circuit_as_motive()
    {
        var lead = await LeadAsync();
        _sender.Send(Arg.Any<CloseLeadCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());

        var outcome = await RunAsync(WorkflowEndActions.LeadLose, "Lead", lead.Id, approved: false);

        outcome.Message.Should().Be("Lead marqué perdu par le circuit.");
        await _sender.Received(1).Send(
            Arg.Is<CloseLeadCommand>(c => c.LeadId == lead.Id && c.Reason == LeadCloseReason.Lost && c.Detail!.Contains("Comité crédit")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Winning_a_project_goes_through_the_close_command_and_a_refusal_is_reported()
    {
        var project = await ProjectAsync();
        _sender.Send(Arg.Any<CloseOpportunityCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<CloseOpportunityResult>("Étape bloquante non franchie : Comité (Crédit)"));

        var outcome = await RunAsync(WorkflowEndActions.ProjectWin, "Opportunity", project.Id);

        outcome.Done.Should().BeFalse();
        outcome.Message.Should().Be("Action non faite : Étape bloquante non franchie : Comité (Crédit)");
        await _sender.Received(1).Send(
            Arg.Is<CloseOpportunityCommand>(c => c.OpportunityId == project.Id && c.Outcome == OpportunityStage.ClosedWon),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_action_of_another_entity_is_ignored()
    {
        var lead = await LeadAsync();

        var outcome = await RunAsync(WorkflowEndActions.ProjectWin, "Lead", lead.Id);

        outcome.Should().BeNull();
        await _sender.DidNotReceiveWithAnyArgs().Send(default(CloseOpportunityCommand)!, default);
    }
}
