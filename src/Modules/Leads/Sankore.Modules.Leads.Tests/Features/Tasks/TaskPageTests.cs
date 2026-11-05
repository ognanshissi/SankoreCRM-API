namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.ListTasks;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>« Toutes les tâches » (2026-11-05): every task the caller may see, one page at a time.</summary>
public sealed class TaskPageTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _awa = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public TaskPageTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetTeamAgentIdsAsync(_tenantId, _manager, Arg.Any<CancellationToken>()).Returns([_awa]);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_tab_lists_what_its_count_says()
    {
        var later = Task(_awa, "Plus tard", days: 3);
        var late = Task(_awa, "En retard", days: -2);
        var slaLate = Task(_awa, "SLA dépassé", days: 3, slaDays: -1);
        await SeedAsync(later, late, slaLate);

        var open = await PageAsync(_manager, "BranchManager", TaskPageView.Open);
        open.Counts.Should().Be(new TaskPageCountsDto(3, 2, 0, 0, 3));
        open.TotalCount.Should().Be(3);
        open.Items[0].Id.Should().Be(late.Id, "ce qui reste à faire se lit par échéance la plus proche");

        var overdue = await PageAsync(_manager, "BranchManager", TaskPageView.Overdue);
        overdue.Items.Select(t => t.Id).Should().BeEquivalentTo([late.Id, slaLate.Id]);
    }

    [Fact]
    public async Task A_manager_cannot_read_someone_outside_their_team_by_naming_them()
    {
        await SeedAsync(Task(_awa, "Awa"), Task(_outsider, "Hors équipe"));

        (await PageAsync(_manager, "BranchManager", TaskPageView.All, agentId: _outsider)).Items.Should().BeEmpty();
        (await PageAsync(_manager, "BranchManager", TaskPageView.All, agentId: _awa)).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task An_agent_sees_only_their_own_tasks_even_when_naming_a_colleague()
    {
        await SeedAsync(Task(_awa, "Awa"), Task(_outsider, "Collègue"));

        var page = await PageAsync(_awa, "Agent", TaskPageView.All, agentId: _outsider);

        page.Items.Should().ContainSingle().Which.AssignedAgentId.Should().Be(_awa);
    }

    [Fact]
    public async Task Tasks_nobody_holds_reach_the_roles_that_see_every_agency_only()
    {
        await SeedAsync(Task(null, "Sans porteur"));

        (await PageAsync(_manager, "Administrator", TaskPageView.All)).Items.Should().ContainSingle();
        (await PageAsync(_manager, "BranchManager", TaskPageView.All)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_search_reads_the_title()
    {
        await SeedAsync(Task(_awa, "Rappeler Madame Koné"), Task(_awa, "Relancer le dossier"));

        var page = await PageAsync(_manager, "BranchManager", TaskPageView.All, search: "kon");

        page.Items.Should().ContainSingle().Which.Title.Should().Be("Rappeler Madame Koné");
    }

    private CrmTask Task(Guid? agent, string title, int days = 1, int? slaDays = null) =>
        CrmTask.Create(_tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, title,
            DateTimeOffset.UtcNow.AddDays(days), assignedAgentId: agent,
            slaDeadline: slaDays is { } sla ? DateTimeOffset.UtcNow.AddDays(sla) : null);

    private async Task SeedAsync(params CrmTask[] tasks)
    {
        await using var db = _db.CreateContext();
        db.CrmTasks.AddRange(tasks);
        await db.SaveChangesAsync();
    }

    private async Task<CrmTaskPageDto> PageAsync(
        Guid userId, string role, TaskPageView view, Guid? agentId = null, string? search = null)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(userId);
        currentUser.Roles.Returns([role]);
        await using var db = _db.CreateContext();
        var tenant = new FixedTenantContext(_tenantId);
        var result = await new GetTaskPageHandler(db, tenant, currentUser, _admin, new TaskTypeCatalog(db, tenant), TimeProvider.System)
            .Handle(new GetTaskPageQuery(view, agentId, search), default);
        return result.Value!;
    }
}
