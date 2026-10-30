namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.ListTasks;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The "Équipe" view of the tasks screen asks for tasks without naming an agent: the server scopes
/// a supervisor to their team, and an agent to themselves.
/// </summary>
public sealed class ListTasksScopingTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _awa = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public ListTasksScopingTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_supervisor_sees_the_tasks_of_their_team_only()
    {
        await AddTasksAsync();
        Team(_awa);

        var tasks = await ListAsync(_manager, "BranchManager");

        tasks.Should().OnlyContain(t => t.AssignedAgentId == _awa);
    }

    [Fact]
    public async Task A_supervisor_with_nobody_to_supervise_sees_nothing_rather_than_everything()
    {
        await AddTasksAsync();
        Team();

        (await ListAsync(_manager, "BranchManager")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_supervisor_also_sees_the_tasks_assigned_to_themselves()
    {
        // GetTeamAgentIdsAsync leaves the caller out: a branch manager who held a lead's
        // first-contact task saw it neither on the lead's Tâches tab nor in the team view.
        await AddTasksAsync();
        var leadId = Guid.NewGuid();
        await using (var db = _db.CreateContext())
        {
            db.CrmTasks.Add(CrmTask.Create(
                _tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, "Premier contact",
                DateTimeOffset.UtcNow.AddDays(1), leadId: leadId, assignedAgentId: _manager));
            await db.SaveChangesAsync();
        }
        Team(_awa);

        var all = await ListAsync(_manager, "BranchManager");
        all.Select(t => t.AssignedAgentId).Should().BeEquivalentTo(new Guid?[] { _awa, _manager });

        var onLead = await ListAsync(_manager, "BranchManager", leadId);
        onLead.Should().ContainSingle().Which.AssignedAgentId.Should().Be(_manager);
    }

    [Fact]
    public async Task An_agent_sees_only_their_own_tasks()
    {
        await AddTasksAsync();

        var tasks = await ListAsync(_awa, "Agent");

        tasks.Should().OnlyContain(t => t.AssignedAgentId == _awa).And.NotBeEmpty();
    }

    private void Team(params Guid[] agents) =>
        _admin.GetTeamAgentIdsAsync(_tenantId, _manager, Arg.Any<CancellationToken>())
            .Returns(agents.ToList());

    private async Task<IReadOnlyList<Sankore.Modules.Leads.Features.Tasks.GetTask.CrmTaskDto>> ListAsync(
        Guid userId, string role, Guid? leadId = null)
    {
        await using var db = _db.CreateContext();
        var result = await new ListTasksHandler(db, new FixedTenantContext(_tenantId), _admin, new TaskTypeCatalog(db, new FixedTenantContext(_tenantId))).Handle(
            new ListTasksQuery(LeadId: leadId, CurrentUserId: userId, CurrentUserRoles: [role]), default);
        return result.Value!;
    }

    private async Task AddTasksAsync()
    {
        await using var db = _db.CreateContext();
        foreach (var agent in new[] { _awa, _outsider })
            db.CrmTasks.Add(CrmTask.Create(
                _tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, "Premier contact",
                DateTimeOffset.UtcNow.AddDays(1), assignedAgentId: agent));
        await db.SaveChangesAsync();
    }
}
