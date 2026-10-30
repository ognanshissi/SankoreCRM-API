namespace Sankore.Modules.Leads.Tests.Features.TaskTypes;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;
using Sankore.Modules.Leads.Features.TaskGenerationRules.UpdateTaskGenerationRule;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Features.TaskTypes.CreateTaskType;
using Sankore.Modules.Leads.Features.TaskTypes.ListTaskTypes;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.Tasks.CreateTask;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The task types of the settings screen are the ones tasks carry (2026-10-30): before, the screen
/// wrote a table nothing read and every task took a hard-coded enum.
/// </summary>
public sealed class TaskTypesTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public TaskTypesTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(id => id, _ => "Moi"));
        _admin.GetUserSupervisorsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(UserSupervisors.None);
    }

    public void Dispose() => _db.Dispose();

    private TaskTypeCatalog Catalog(LeadsDbContext db) => new(db, new FixedTenantContext(_tenantId));

    private ICurrentUser Me()
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_me);
        user.TenantId.Returns(_tenantId);
        user.Roles.Returns(["Agent"]);
        return user;
    }

    private static ITenantStore StoreOf(params Guid[] tenantIds)
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(tenantIds.Select(id => new TenantInfo(id, "IMF", $"{id:N}.sankore.local", true, false, null, null)).ToList());
        return store;
    }

    private async Task AddTypeAsync(string code, string label, bool active = true)
    {
        await using var db = _db.CreateContext();
        var type = TaskTypeConfig.Create(_tenantId, code, label);
        if (!active) type.Deactivate();
        db.TaskTypeConfigs.Add(type);
        await db.SaveChangesAsync();
    }

    private async Task<Result<Guid>> CreateTaskAsync(string type)
    {
        await using var db = _db.CreateContext();
        var user = Me();
        var subjects = new TaskSubjects(db, user, new LeadAccessGuard(user, _admin),
            Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>(), new LeadVisibility(user));
        return await new CreateTaskHandler(db, user, subjects, Catalog(db), _admin, Substitute.For<IEventPublisher>(), TimeProvider.System)
            .Handle(new CreateTaskCommand(type, CrmTaskPriority.Medium, "Visiter le marché", DateTimeOffset.UtcNow.AddDays(1),
                AssignedAgentId: _me), CancellationToken.None);
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_active_tenant_gets_the_seven_system_types_once()
    {
        var other = Guid.NewGuid();
        await using (var db = _db.CreateContext())
        {
            await TaskTypeSeeder.SeedAsync(db, StoreOf(_tenantId, other), NullLogger.Instance);
            await TaskTypeSeeder.SeedAsync(db, StoreOf(_tenantId, other), NullLogger.Instance);
        }

        await using var read = _db.CreateContext();
        var rows = await read.TaskTypeConfigs.IgnoreQueryFilters().ToListAsync();
        rows.Should().HaveCount(14);
        rows.Where(t => t.TenantId == _tenantId).Select(t => t.Code)
            .Should().BeEquivalentTo(CrmTaskType.System.Select(t => t.Code));
        rows.Should().OnlyContain(t => t.IsSystem && t.IsActive);
    }

    [Fact]
    public async Task A_renamed_system_type_keeps_its_label_across_restarts()
    {
        await using (var db = _db.CreateContext())
        {
            await TaskTypeSeeder.SeedAsync(db, StoreOf(_tenantId), NullLogger.Instance);
            var generic = await db.TaskTypeConfigs.AsTracking().SingleAsync(t => t.Code == CrmTaskType.Generic);
            generic.Update("Tâche libre", null, 1);
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateContext())
            await TaskTypeSeeder.SeedAsync(db, StoreOf(_tenantId), NullLogger.Instance);

        await using var read = _db.CreateContext();
        (await read.TaskTypeConfigs.SingleAsync(t => t.Code == CrmTaskType.Generic)).Label.Should().Be("Tâche libre");
    }

    [Fact]
    public async Task A_tenant_created_since_the_last_boot_is_seeded_on_its_first_listing()
    {
        await using var db = _db.CreateContext();

        var listed = await new ListTaskTypesHandler(db, Catalog(db)).Handle(new ListTaskTypesQuery(), CancellationToken.None);

        listed.Value!.Select(t => t.Label).Should().StartWith("Premier contact").And.HaveCount(7);
        listed.Value!.Should().OnlyContain(t => t.IsSystem);
    }

    // ── Giving a task a type ──────────────────────────────────────────────────

    [Fact]
    public async Task A_task_takes_a_type_the_tenant_added()
    {
        await AddTypeAsync("VISITE_TERRAIN", "Visite terrain");

        var created = await CreateTaskAsync("VISITE_TERRAIN");

        created.IsSuccess.Should().BeTrue(created.Error);
        await using var db = _db.CreateContext();
        (await db.CrmTasks.SingleAsync()).Type.Should().Be("VISITE_TERRAIN");
    }

    [Fact]
    public async Task A_deactivated_type_is_refused_and_an_unknown_one_too()
    {
        await AddTypeAsync("VISITE_TERRAIN", "Visite terrain", active: false);

        (await CreateTaskAsync("VISITE_TERRAIN")).Error.Should().Be(TaskTypeCatalog.InactiveError);
        (await CreateTaskAsync("COMITE")).Error.Should().Be(TaskTypeCatalog.UnknownError);

        await using var db = _db.CreateContext();
        (await db.CrmTasks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_system_type_is_usable_before_its_row_exists()
    {
        (await CreateTaskAsync(CrmTaskType.Generic)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_task_is_read_with_the_label_of_the_settings()
    {
        await using (var db = _db.CreateContext())
        {
            await TaskTypeSeeder.SeedAsync(db, StoreOf(_tenantId), NullLogger.Instance);
            var generic = await db.TaskTypeConfigs.AsTracking().SingleAsync(t => t.Code == CrmTaskType.Generic);
            generic.Update("Tâche libre", null, 1);
            await db.SaveChangesAsync();
        }
        var created = await CreateTaskAsync(CrmTaskType.Generic);

        await using var read = _db.CreateContext();
        var access = new TaskAccess(read, Me(), _admin, NullLogger<TaskAccess>.Instance);
        var task = await new GetTaskHandler(access, Catalog(read)).Handle(new GetTaskQuery(created.Value), CancellationToken.None);

        task.Value!.Type.Should().Be(CrmTaskType.Generic);
        task.Value.TypeLabel.Should().Be("Tâche libre");
    }

    [Fact]
    public async Task A_system_code_falls_back_to_its_seeded_label_and_any_other_code_to_itself()
    {
        await AddTypeAsync("VISITE_TERRAIN", "Visite terrain");
        await using var db = _db.CreateContext();

        var labels = await Catalog(db).LabelsAsync(CancellationToken.None);

        labels[CrmTaskType.FirstContact].Should().Be("Premier contact");
        labels["VISITE_TERRAIN"].Should().Be("Visite terrain");
        labels.GetValueOrDefault("SUPPRIME", "SUPPRIME").Should().Be("SUPPRIME");
    }

    // ── The settings and the generation rules ─────────────────────────────────

    [Fact]
    public async Task A_tenant_cannot_create_a_type_with_a_system_code()
    {
        await using var db = _db.CreateContext();

        var created = await new CreateTaskTypeHandler(db)
            .Handle(new CreateTaskTypeCommand(_tenantId, CrmTaskType.FirstContact, "Mon premier contact", null, 0), CancellationToken.None);

        created.Error.Should().Be("TASK_TYPE_CODE_ALREADY_EXISTS");
    }

    [Fact]
    public async Task A_rule_cannot_be_given_a_deactivated_type_but_keeps_one_it_already_has()
    {
        await AddTypeAsync("VISITE_TERRAIN", "Visite terrain");
        Guid ruleId;
        await using (var db = _db.CreateContext())
        {
            var created = await new CreateTaskGenerationRuleHandler(db, Catalog(db)).Handle(new CreateTaskGenerationRuleCommand(
                _tenantId, TaskTriggerEvents.LeadDispatched, "VISITE_TERRAIN", CrmTaskPriority.High,
                "Visiter", TimeSpan.FromHours(4), TimeSpan.FromDays(1)), CancellationToken.None);
            created.IsSuccess.Should().BeTrue(created.Error);
            ruleId = created.Value;

            var type = await db.TaskTypeConfigs.AsTracking().SingleAsync(t => t.Code == "VISITE_TERRAIN");
            type.Deactivate();
            await db.SaveChangesAsync();
        }

        await using var update = _db.CreateContext();
        var handler = new UpdateTaskGenerationRuleHandler(update, Catalog(update));
        (await handler.Handle(new UpdateTaskGenerationRuleCommand(ruleId, TaskTriggerEvents.LeadDispatched, "VISITE_TERRAIN",
            CrmTaskPriority.Medium, "Visiter le marché", TimeSpan.FromHours(4), TimeSpan.FromDays(1)), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var refused = await new CreateTaskGenerationRuleHandler(update, Catalog(update)).Handle(new CreateTaskGenerationRuleCommand(
            _tenantId, TaskTriggerEvents.LeadDispatched, "VISITE_TERRAIN", CrmTaskPriority.High,
            "Visiter", TimeSpan.FromHours(4), TimeSpan.FromDays(1)), CancellationToken.None);
        refused.Error.Should().Be(TaskTypeCatalog.InactiveError);
    }
}
