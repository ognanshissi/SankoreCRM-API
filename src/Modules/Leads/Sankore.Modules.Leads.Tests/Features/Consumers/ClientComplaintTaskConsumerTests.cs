namespace Sankore.Modules.Leads.Tests.Features.Consumers;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Consumers;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>The task of a client complaint (2026-10-26): one per complaint and per reason, about the client.</summary>
public sealed class ClientComplaintTaskConsumerTests : IDisposable
{
    private static readonly DateTimeOffset Due = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _complaintId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public ClientComplaintTaskConsumerTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    private async Task ConsumeAsync(string reason)
    {
        var context = Substitute.For<ConsumeContext<ClientComplaintActionRequiredEvent>>();
        context.Message.Returns(new ClientComplaintActionRequiredEvent(
            _tenantId, _clientId, _complaintId, "RCL-2026-000001", "Frais prélevés deux fois", _agent, reason, Due));
        context.CancellationToken.Returns(CancellationToken.None);

        await using var db = _db.CreateContext();
        await new ClientComplaintTaskConsumer(db, TimeProvider.System).Consume(context);
    }

    [Fact]
    public async Task A_recorded_complaint_gives_its_assignee_one_task_about_the_client_due_at_the_acknowledgement()
    {
        await ConsumeAsync(ClientComplaintActionReasons.Filed);
        await ConsumeAsync(ClientComplaintActionReasons.Filed);

        await using var db = _db.CreateContext();
        var task = await db.CrmTasks.SingleAsync();
        task.Title.Should().Be("Traiter la réclamation RCL-2026-000001");
        task.AssignedAgentId.Should().Be(_agent);
        task.EntityType.Should().Be(TaskEntityType.Client);
        task.EntityId.Should().Be(_clientId);
        task.DueAt.Should().Be(Due);
        task.Priority.Should().Be(CrmTaskPriority.High);
    }

    private async Task ProgressAsync(string stage)
    {
        var context = Substitute.For<ConsumeContext<ClientComplaintProgressedEvent>>();
        context.Message.Returns(new ClientComplaintProgressedEvent(_tenantId, _clientId, _complaintId, stage));
        context.CancellationToken.Returns(CancellationToken.None);

        await using var db = _db.CreateContext();
        await new ClientComplaintTaskConsumer(db, TimeProvider.System).Consume(context);
    }

    private async Task<Dictionary<string, CrmTaskStatus>> StatusesAsync()
    {
        await using var db = _db.CreateContext();
        return await db.CrmTasks.ToDictionaryAsync(t => t.TriggerEventType!, t => t.Status);
    }

    [Fact]
    public async Task An_acknowledgement_closes_only_the_late_acknowledgement_task()
    {
        await ConsumeAsync(ClientComplaintActionReasons.Filed);
        await ConsumeAsync(ClientComplaintActionReasons.AcknowledgementOverdue);

        await ProgressAsync(ClientComplaintStages.Acknowledged);

        var statuses = await StatusesAsync();
        statuses["ClientComplaintFiled"].Should().Be(CrmTaskStatus.Pending, "the assignee still owes the answer");
        statuses["ClientComplaintAcknowledgementOverdue"].Should().Be(CrmTaskStatus.Completed);
    }

    [Fact]
    public async Task An_answer_completes_every_task_and_a_closure_cancels_them()
    {
        await ConsumeAsync(ClientComplaintActionReasons.Filed);
        await ConsumeAsync(ClientComplaintActionReasons.ResolutionOverdue);
        await ProgressAsync(ClientComplaintStages.Resolved);
        await ProgressAsync(ClientComplaintStages.Resolved);

        (await StatusesAsync()).Values.Should().OnlyContain(s => s == CrmTaskStatus.Completed,
            "a redelivered event finds nothing left to close");

        await using (var db = _db.CreateContext())
        {
            db.CrmTasks.RemoveRange(db.CrmTasks);
            await db.SaveChangesAsync();
        }

        await ConsumeAsync(ClientComplaintActionReasons.Filed);
        await ProgressAsync(ClientComplaintStages.Closed);

        (await StatusesAsync())["ClientComplaintFiled"].Should().Be(CrmTaskStatus.Cancelled, "a withdrawn complaint leaves nothing to do");
    }

    [Fact]
    public async Task A_late_deadline_is_a_second_critical_task_not_a_duplicate_of_the_first()
    {
        await ConsumeAsync(ClientComplaintActionReasons.Filed);
        await ConsumeAsync(ClientComplaintActionReasons.AcknowledgementOverdue);

        await using var db = _db.CreateContext();
        var tasks = await db.CrmTasks.OrderBy(t => t.CreatedAt).ToListAsync();
        tasks.Should().HaveCount(2);
        tasks.Should().ContainSingle(t => t.Priority == CrmTaskPriority.Critical
                                       && t.Title == "Accusé de réception en retard : réclamation RCL-2026-000001");
    }
}
