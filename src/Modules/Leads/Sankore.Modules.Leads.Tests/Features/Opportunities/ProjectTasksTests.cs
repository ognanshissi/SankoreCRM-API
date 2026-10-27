namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.ProjectTasks;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.Tasks.CreateTask;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Tasks attached to a project (owner, 2026-10-27): created on it, listed on its page.</summary>
public sealed class ProjectTasksTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public ProjectTasksTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(id => id, id => id == _me ? "Moi" : "Collègue"));
        _admin.GetUserSupervisorsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(UserSupervisors.None);
        _admin.GetTeamAgentIdsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>An agent: authenticated, without <c>lead:read:all</c> — sees the projects they own.</summary>
    private ICurrentUser Agent(Guid id)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.TenantId.Returns(_tenantId);
        user.IsAuthenticated.Returns(true);
        user.Roles.Returns(["Agent"]);
        user.Permissions.Returns([]);
        return user;
    }

    private async Task<Opportunity> ProjectOwnedByAsync(Guid owner)
    {
        await using var db = _db.CreateContext();
        var project = Opportunity.CreateForCustomer(_tenantId, Guid.NewGuid(), "Obsèques Awa", "OBS", owner, TimeProvider.System);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private async Task<Result<Guid>> CreateAsync(ICurrentUser user, Guid projectId, Guid holder, DateTimeOffset dueAt, string title = "Relancer")
    {
        await using var db = _db.CreateContext();
        var subjects = new TaskSubjects(db, user, new LeadAccessGuard(user, _admin),
            Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>(), new LeadVisibility(user));
        return await new CreateTaskHandler(db, user, subjects, _admin, Substitute.For<IEventPublisher>(), TimeProvider.System)
            .Handle(new CreateTaskCommand(CrmTaskType.Generic, CrmTaskPriority.Medium, title, dueAt,
                AssignedAgentId: holder, EntityType: TaskEntityType.Project, EntityId: projectId), CancellationToken.None);
    }

    private async Task<Result<IReadOnlyList<ProjectTaskDto>>> ListAsync(ICurrentUser user, Guid projectId)
    {
        await using var db = _db.CreateContext();
        var tenant = Substitute.For<ITenantContext>();
        tenant.CurrentTenantId.Returns(_tenantId);
        return await new ListProjectTasksHandler(db, new TaskAccess(db, user, _admin, NullLogger<TaskAccess>.Instance),
                _admin, tenant, new LeadVisibility(user))
            .Handle(new ListProjectTasksQuery(projectId), CancellationToken.None);
    }

    [Fact]
    public async Task A_task_on_a_project_the_creator_can_open_is_attached_to_it()
    {
        var project = await ProjectOwnedByAsync(_me);

        var created = await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(1));

        created.IsSuccess.Should().BeTrue(created.Error);
        await using var db = _db.CreateContext();
        var task = await db.CrmTasks.SingleAsync();
        task.EntityType.Should().Be(TaskEntityType.Project);
        task.EntityId.Should().Be(project.Id);
        task.LeadId.Should().BeNull();
    }

    [Fact]
    public async Task A_project_out_of_the_creator_s_perimeter_reads_as_absent()
    {
        var project = await ProjectOwnedByAsync(_colleague);

        (await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(1))).Error.Should().Be("OPPORTUNITY_NOT_FOUND");
        (await CreateAsync(Agent(_me), Guid.NewGuid(), _me, _now.AddDays(1))).Error.Should().Be("OPPORTUNITY_NOT_FOUND");
    }

    [Fact]
    public async Task The_subject_is_named_after_the_project()
    {
        var project = await ProjectOwnedByAsync(_me);
        (await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(1))).IsSuccess.Should().BeTrue();

        await using var db = _db.CreateContext();
        var user = Agent(_me);
        var subjects = new TaskSubjects(db, user, new LeadAccessGuard(user, _admin),
            Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>(), new LeadVisibility(user));

        var subject = await subjects.DescribeAsync(await db.CrmTasks.SingleAsync(), CancellationToken.None);

        subject.Should().Be(new TaskSubjectDto("Project", project.Id, "Obsèques Awa"));
    }

    [Fact]
    public async Task The_page_lists_open_tasks_by_due_date_then_closed_ones_with_their_holder()
    {
        var project = await ProjectOwnedByAsync(_me);
        var later = (await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(5), "Signature")).Value;
        var sooner = (await CreateAsync(Agent(_me), project.Id, _colleague, _now.AddDays(1), "Pièces")).Value;
        var done = (await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(2), "Devis")).Value;
        await using (var db = _db.CreateContext())
        {
            var task = await db.CrmTasks.AsTracking().SingleAsync(t => t.Id == done);
            task.Complete();
            await db.SaveChangesAsync();
        }

        var tasks = (await ListAsync(Agent(_me), project.Id)).Value!;

        tasks.Select(t => t.Id).Should().Equal(sooner, later, done);
        tasks[0].AssignedAgentName.Should().Be("Collègue");
        tasks[2].Status.Should().Be(CrmTaskStatus.Completed);
    }

    [Fact]
    public async Task Only_the_tasks_the_caller_may_open_are_listed()
    {
        var project = await ProjectOwnedByAsync(_me);
        var mine = (await CreateAsync(Agent(_me), project.Id, _me, _now.AddDays(1))).Value;
        await using (var db = _db.CreateContext())
        {
            // A colleague's own task on the same project, which they created and hold: not mine to open.
            var theirs = CrmTask.CreateManual(_tenantId, _colleague, CrmTaskType.Generic, CrmTaskPriority.Low, "Note perso",
                _now.AddDays(1), TaskEntityType.Project, project.Id, _colleague, null, _now).Value!;
            db.CrmTasks.Add(theirs);
            await db.SaveChangesAsync();
        }

        var tasks = (await ListAsync(Agent(_me), project.Id)).Value!;

        tasks.Select(t => t.Id).Should().Equal(mine);
    }

    [Fact]
    public async Task A_project_the_caller_cannot_open_has_no_tasks_to_show()
    {
        var project = await ProjectOwnedByAsync(_colleague);

        (await ListAsync(Agent(_me), project.Id)).Error.Should().Be(OpportunityErrors.NotFound);
    }

    [Fact]
    public async Task Tasks_about_other_records_are_not_the_project_s()
    {
        var project = await ProjectOwnedByAsync(_me);
        await using (var db = _db.CreateContext())
        {
            db.CrmTasks.Add(CrmTask.CreateManual(_tenantId, _me, CrmTaskType.Generic, CrmTaskPriority.Low, "Client",
                _now.AddDays(1), TaskEntityType.Client, project.CustomerEntityId, _me, null, _now).Value!);
            await db.SaveChangesAsync();
        }

        (await ListAsync(Agent(_me), project.Id)).Value.Should().BeEmpty();
    }
}
