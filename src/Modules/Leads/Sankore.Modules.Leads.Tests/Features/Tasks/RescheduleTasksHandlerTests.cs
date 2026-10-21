namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.RescheduleTasks;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class RescheduleTasksHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public RescheduleTasksHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.Roles.Returns(["Agent"]);
    }

    public void Dispose() => _db.Dispose();

    private async Task<CrmTask> SeedAsync(Guid? agentId, DateTimeOffset? sla = null)
    {
        var task = CrmTask.Create(_tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, "Premier contact",
            _now.AddHours(2), leadId: Guid.NewGuid(), assignedAgentId: agentId, slaDeadline: sla);
        await using var db = _db.CreateContext();
        db.CrmTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private async Task<Sankore.Shared.Kernel.Result> RunAsync(params TaskDueDateInput[] items)
    {
        await using var db = _db.CreateContext();
        return await new RescheduleTasksHandler(db, _currentUser, TimeProvider.System)
            .Handle(new RescheduleTasksCommand(items), CancellationToken.None);
    }

    private async Task<CrmTask> ReloadAsync(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.CrmTasks.SingleAsync(t => t.Id == id);
    }

    [Fact]
    public async Task Postponing_moves_the_due_date_and_leaves_the_sla_alone()
    {
        var sla = _now.AddHours(4);
        var task = await SeedAsync(_me, sla);
        var tomorrow = _now.AddDays(1);

        (await RunAsync(new TaskDueDateInput(task.Id, tomorrow))).IsSuccess.Should().BeTrue();

        var saved = await ReloadAsync(task.Id);
        saved.DueAt.Should().Be(tomorrow);
        saved.SlaDeadline.Should().Be(sla);
    }

    [Fact]
    public async Task A_batch_is_all_or_nothing()
    {
        var mine = await SeedAsync(_me);
        var someoneElses = await SeedAsync(Guid.NewGuid());

        var result = await RunAsync(
            new TaskDueDateInput(mine.Id, _now.AddDays(1)),
            new TaskDueDateInput(someoneElses.Id, _now.AddDays(1)));

        result.Error.Should().Be("NOT_TASK_OWNER");
        (await ReloadAsync(mine.Id)).DueAt.Should().Be(mine.DueAt);
    }

    [Fact]
    public async Task A_date_in_the_past_is_refused()
    {
        var task = await SeedAsync(_me);

        (await RunAsync(new TaskDueDateInput(task.Id, _now.AddHours(-1)))).Error.Should().Be("TASK_DUE_IN_PAST");
    }

    [Fact]
    public async Task A_supervisor_may_move_an_agents_task()
    {
        _currentUser.Roles.Returns(["BranchManager"]);
        var task = await SeedAsync(Guid.NewGuid());

        (await RunAsync(new TaskDueDateInput(task.Id, _now.AddDays(2)))).IsSuccess.Should().BeTrue();
    }
}
