namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

public sealed class AgentAbsenceTaskTransferConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _absent = Guid.NewGuid();
    private readonly Guid _replacement = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public AgentAbsenceTaskTransferConsumerTests() => _factory = new TestDbContextFactory(_tenantId);
    public void Dispose() => _factory.Dispose();

    private CrmTask SeedTask(Guid agentId, bool completed = false)
    {
        var task = CrmTask.Create(_tenantId, CrmTaskType.Qualification, CrmTaskPriority.Medium,
            "Qualifier", DateTimeOffset.UtcNow.AddHours(4), assignedAgentId: agentId);
        if (completed) task.Complete();

        using var db = _factory.CreateContext();
        db.CrmTasks.Add(task);
        db.SaveChanges();
        return task;
    }

    private async Task Consume(bool transfer)
    {
        var context = Substitute.For<ConsumeContext<AgentAbsenceDeclaredEvent>>();
        context.Message.Returns(new AgentAbsenceDeclaredEvent(
            _tenantId, _absent, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), _replacement, transfer));
        context.CancellationToken.Returns(CancellationToken.None);

        await new AgentAbsenceTaskTransferConsumer(
            _factory.CreateContext(), NullLogger<AgentAbsenceTaskTransferConsumer>.Instance).Consume(context);
    }

    [Fact]
    public async Task Open_tasks_move_to_the_replacement_with_a_reassignment_record()
    {
        var open = SeedTask(_absent);
        var done = SeedTask(_absent, completed: true);
        var colleague = SeedTask(Guid.NewGuid());

        await Consume(transfer: true);

        await using var db = _factory.CreateContext();
        var tasks = await db.CrmTasks.ToDictionaryAsync(t => t.Id);
        tasks[open.Id].AssignedAgentId.Should().Be(_replacement);
        tasks[done.Id].AssignedAgentId.Should().Be(_absent, "une tâche terminée n'est pas à reprendre");
        tasks[colleague.Id].AssignedAgentId.Should().NotBe(_replacement);
        (await db.TaskReassignments.SingleAsync()).Reason.Should().Contain("19/10/2026");
    }

    [Fact]
    public async Task Nothing_moves_when_the_agent_did_not_ask_for_a_transfer()
    {
        var open = SeedTask(_absent);

        await Consume(transfer: false);

        await using var db = _factory.CreateContext();
        (await db.CrmTasks.SingleAsync(t => t.Id == open.Id)).AssignedAgentId.Should().Be(_absent);
    }

    [Fact]
    public async Task A_redelivered_event_transfers_nothing_twice()
    {
        SeedTask(_absent);

        await Consume(transfer: true);
        await Consume(transfer: true);

        await using var db = _factory.CreateContext();
        (await db.TaskReassignments.CountAsync()).Should().Be(1);
    }
}
