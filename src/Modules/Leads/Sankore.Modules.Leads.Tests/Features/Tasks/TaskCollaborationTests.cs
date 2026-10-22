namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Tasks.AssignTask;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.Tasks.CreateTask;
using Sankore.Modules.Leads.Features.Tasks.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Tasks people create and work in together: subject, people, criteria, comments, tags, files.</summary>
public sealed class TaskCollaborationTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public TaskCollaborationTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1)
                .Where(id => id == _me || id == _colleague || id == _manager || id == _stranger)
                .ToDictionary(id => id, id => id == _me ? "Moi" : "Collègue"));
        _admin.GetUserSupervisorsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(UserSupervisors.None);
        _admin.GetTeamAgentIdsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    public void Dispose() => _db.Dispose();

    private ICurrentUser User(Guid id, params string[] roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.TenantId.Returns(_tenantId);
        user.Roles.Returns(roles.Length == 0 ? ["Agent"] : roles);
        return user;
    }

    private TaskAccess Access(LeadsDbContext db, ICurrentUser user) => new(db, user, _admin, NullLogger<TaskAccess>.Instance);

    private async Task<Result<Guid>> CreateAsync(ICurrentUser user, CreateTaskCommand cmd)
    {
        await using var db = _db.CreateContext();
        var subjects = new TaskSubjects(db, user, new LeadAccessGuard(user, _admin), _customers, _kyc);
        return await new CreateTaskHandler(db, user, subjects, _admin, _publisher, TimeProvider.System).Handle(cmd, CancellationToken.None);
    }

    private CreateTaskCommand Command(
        TaskEntityType? type = null, Guid? entityId = null, Guid? holder = null,
        IReadOnlyList<Guid>? participants = null, IReadOnlyList<string>? criteria = null, IReadOnlyList<string>? tags = null) =>
        new(CrmTaskType.Generic, CrmTaskPriority.Medium, "Relancer le dossier", _now.AddDays(1),
            AssignedAgentId: holder ?? _me, Description: "Appeler avant midi",
            EntityType: type, EntityId: entityId, ParticipantIds: participants, Criteria: criteria, Tags: tags);

    // ── Creation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_task_about_a_client_stores_the_pair_and_no_lead()
    {
        var clientId = Guid.NewGuid();
        _customers.ResolveClientIdAsync(_tenantId, clientId, Arg.Any<CancellationToken>()).Returns(clientId);
        _customers.CanActorAccessClientAsync(_tenantId, clientId, _me, Arg.Any<CancellationToken>()).Returns(true);

        var created = await CreateAsync(User(_me), Command(TaskEntityType.Client, clientId,
            participants: [_colleague], criteria: ["Pièce reçue", "Signature"], tags: [" Urgent ", "urgent", "Crédit"]));

        created.IsSuccess.Should().BeTrue(created.Error);
        await using var db = _db.CreateContext();
        var task = await db.CrmTasks.SingleAsync();
        task.EntityType.Should().Be(TaskEntityType.Client);
        task.EntityId.Should().Be(clientId);
        task.LeadId.Should().BeNull();
        task.CreatedBy.Should().Be(_me);
        (await db.CrmTaskParticipants.Select(p => p.UserId).ToListAsync()).Should().Equal(_colleague);
        (await db.CrmTaskCriteria.OrderBy(c => c.Position).Select(c => c.Label).ToListAsync()).Should().Equal("Pièce reçue", "Signature");
        (await db.CrmTaskTags.Select(t => t.Tag).OrderBy(t => t).ToListAsync()).Should().Equal("crédit", "urgent");

        // My own task: nobody is told I gave it to myself — only the associated colleague is.
        await _publisher.DidNotReceive().PublishAsync(Arg.Any<TaskAssignedIntegrationEvent>(), Arg.Any<CancellationToken>());
        await _publisher.Received(1).PublishAsync(
            Arg.Is<CrmTaskActivityEvent>(e => e.Kind == TaskActivityKind.Associated && e.RecipientIds.Single() == _colleague),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_subject_out_of_reach_reads_as_absent()
    {
        var fileId = Guid.NewGuid();
        _kyc.CanActorAccessKycFileAsync(_tenantId, fileId, _me, Arg.Any<CancellationToken>()).Returns(false);

        (await CreateAsync(User(_me), Command(TaskEntityType.KycFile, fileId))).Error.Should().Be("KYC_FILE_NOT_FOUND");
        (await CreateAsync(User(_me), Command(TaskEntityType.Client, Guid.NewGuid()))).Error.Should().Be("CLIENT_NOT_FOUND");
        (await CreateAsync(User(_me), Command(TaskEntityType.Lead, Guid.NewGuid()))).Error.Should().Be("LEAD_NOT_FOUND");
    }

    [Fact]
    public async Task A_task_handed_to_a_colleague_tells_them_by_mail_and_in_the_bell()
    {
        (await CreateAsync(User(_me), Command(holder: _colleague))).IsSuccess.Should().BeTrue();

        await _publisher.Received(1).PublishAsync(Arg.Is<TaskAssignedIntegrationEvent>(e => e.AgentId == _colleague), Arg.Any<CancellationToken>());
        await _publisher.Received(1).PublishAsync(
            Arg.Is<CrmTaskActivityEvent>(e => e.Kind == TaskActivityKind.Assigned && e.RecipientIds.Single() == _colleague),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_user_cannot_be_associated()
    {
        (await CreateAsync(User(_me), Command(participants: [Guid.NewGuid()]))).Error.Should().Be("TASK_USER_NOT_FOUND");
    }

    [Fact]
    public void A_task_about_a_lead_keeps_lead_id_in_step()
    {
        var leadId = Guid.NewGuid();
        var task = CrmTask.CreateManual(_tenantId, _me, CrmTaskType.Generic, CrmTaskPriority.Low, "Appel", _now.AddHours(1),
            TaskEntityType.Lead, leadId, _me, null, _now).Value!;

        task.LeadId.Should().Be(leadId);
        task.EntityId.Should().Be(leadId);

        CrmTask.CreateManual(_tenantId, _me, CrmTaskType.Generic, CrmTaskPriority.Low, "Appel", _now.AddHours(1),
            TaskEntityType.Client, null, _me, null, _now).Error.Should().Be("TASK_ENTITY_INCOMPLETE");
    }

    // ── Who may open a task ───────────────────────────────────────────────────

    private async Task<CrmTask> SeedTaskAsync(Guid holder)
    {
        var task = CrmTask.CreateManual(_tenantId, holder, CrmTaskType.Generic, CrmTaskPriority.Medium, "Tâche", _now.AddDays(1),
            null, null, holder, null, _now).Value!;
        await using var db = _db.CreateContext();
        db.CrmTasks.Add(task);
        db.CrmTaskParticipants.Add(CrmTaskParticipant.Add(_tenantId, task.Id, _colleague, holder, _now));
        await db.SaveChangesAsync();
        return task;
    }

    private async Task<bool> CanOpenAsync(Guid taskId, ICurrentUser user)
    {
        await using var db = _db.CreateContext();
        return (await Access(db, user).LoadAsync(taskId, tracking: false, CancellationToken.None)).IsSuccess;
    }

    [Fact]
    public async Task The_people_of_the_task_and_the_holders_managers_open_it_and_nobody_else()
    {
        var task = await SeedTaskAsync(_me);

        (await CanOpenAsync(task.Id, User(_me))).Should().BeTrue("the holder");
        (await CanOpenAsync(task.Id, User(_colleague))).Should().BeTrue("an associated colleague");
        (await CanOpenAsync(task.Id, User(_stranger))).Should().BeFalse("someone with no part in it");
        (await CanOpenAsync(task.Id, User(_stranger, "Administrator"))).Should().BeTrue("a bypass role");

        _admin.GetUserSupervisorsAsync(_tenantId, _me, Arg.Any<CancellationToken>()).Returns(new UserSupervisors(_manager, null));
        (await CanOpenAsync(task.Id, User(_manager))).Should().BeTrue("the holder's line manager");

        _admin.GetUserSupervisorsAsync(_tenantId, _me, Arg.Any<CancellationToken>()).Returns(new UserSupervisors(null, _stranger));
        (await CanOpenAsync(task.Id, User(_stranger))).Should().BeTrue("the head of the holder's agency");
    }

    [Fact]
    public async Task A_failing_supervision_lookup_refuses()
    {
        var task = await SeedTaskAsync(_me);
        _admin.GetUserSupervisorsAsync(_tenantId, _me, Arg.Any<CancellationToken>()).Returns<UserSupervisors>(_ => throw new TimeoutException());

        (await CanOpenAsync(task.Id, User(_manager))).Should().BeFalse();
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    private TaskCommentHandlers Comments(LeadsDbContext db, ICurrentUser user) =>
        new(db, Access(db, user), user, _admin, _publisher, TimeProvider.System);

    [Fact]
    public async Task A_comment_tells_the_others_and_only_its_author_edits_or_deletes_it()
    {
        var task = await SeedTaskAsync(_me);
        Guid commentId;

        await using (var db = _db.CreateContext())
        {
            var added = await Comments(db, User(_colleague)).Handle(new AddTaskCommentCommand(task.Id, "  Pièce reçue ce matin  "), default);
            added.IsSuccess.Should().BeTrue(added.Error);
            commentId = added.Value;
        }

        await _publisher.Received(1).PublishAsync(
            Arg.Is<CrmTaskActivityEvent>(e => e.Kind == TaskActivityKind.Commented && e.RecipientIds.SequenceEqual(new[] { _me })),
            Arg.Any<CancellationToken>());

        await using (var db = _db.CreateContext())
            (await Comments(db, User(_me)).Handle(new EditTaskCommentCommand(task.Id, commentId, "Autre"), default))
                .Error.Should().Be("TASK_COMMENT_NOT_YOURS");

        await using (var db = _db.CreateContext())
            (await Comments(db, User(_colleague)).Handle(new DeleteTaskCommentCommand(task.Id, commentId), default)).IsSuccess.Should().BeTrue();

        await using (var db = _db.CreateContext())
        {
            var page = await Comments(db, User(_me)).Handle(new ListTaskCommentsQuery(task.Id, null, 50), default);
            var comment = page.Value!.Items.Single();
            comment.IsDeleted.Should().BeTrue();
            comment.Body.Should().BeNull();
        }
    }

    [Fact]
    public async Task Comments_page_from_the_newest_and_read_oldest_first()
    {
        var task = await SeedTaskAsync(_me);
        await using (var db = _db.CreateContext())
        {
            for (var i = 0; i < 5; i++)
                db.CrmTaskComments.Add(CrmTaskComment.Write(_tenantId, task.Id, _me, $"c{i}", _now.AddMinutes(i)).Value!);
            await db.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var first = (await Comments(read, User(_me)).Handle(new ListTaskCommentsQuery(task.Id, null, 2), default)).Value!;
        first.Items.Select(c => c.Body).Should().Equal("c3", "c4");
        first.NextBefore.Should().NotBeNull();

        var older = (await Comments(read, User(_me)).Handle(new ListTaskCommentsQuery(task.Id, first.NextBefore, 10), default)).Value!;
        older.Items.Select(c => c.Body).Should().Equal("c0", "c1", "c2");
        older.NextBefore.Should().BeNull();
    }

    // ── Criteria, tags ────────────────────────────────────────────────────────

    [Fact]
    public async Task Ticking_a_criterion_records_who_and_a_closed_task_freezes_them()
    {
        var task = await SeedTaskAsync(_me);
        Guid criterionId;

        await using (var db = _db.CreateContext())
        {
            var handlers = new TaskCriteriaHandlers(db, Access(db, User(_colleague)), User(_colleague), TimeProvider.System);
            criterionId = (await handlers.Handle(new AddTaskCriterionCommand(task.Id, "Pièce reçue"), default)).Value;
            (await handlers.Handle(new UpdateTaskCriterionCommand(task.Id, criterionId, null, true), default)).IsSuccess.Should().BeTrue();
        }

        await using (var db = _db.CreateContext())
        {
            var criterion = await db.CrmTaskCriteria.SingleAsync();
            criterion.IsMet.Should().BeTrue();
            criterion.MetBy.Should().Be(_colleague);

            var tracked = await db.CrmTasks.AsTracking().SingleAsync();
            tracked.Complete();
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateContext())
        {
            var handlers = new TaskCriteriaHandlers(db, Access(db, User(_me)), User(_me), TimeProvider.System);
            (await handlers.Handle(new UpdateTaskCriterionCommand(task.Id, criterionId, null, false), default)).Error.Should().Be("TASK_CLOSED");
        }
    }

    [Fact]
    public async Task Tags_are_a_set_normalised_like_a_leads()
    {
        var task = await SeedTaskAsync(_me);

        await using var db = _db.CreateContext();
        var handler = new SetTaskTagsHandler(db, Access(db, User(_me)), User(_me), TimeProvider.System);
        (await handler.Handle(new SetTaskTagsCommand(task.Id, ["Crédit", "URGENT"]), default)).Value.Should().Equal("crédit", "urgent");
        (await handler.Handle(new SetTaskTagsCommand(task.Id, ["urgent", "relance"]), default)).Value.Should().Equal("relance", "urgent");
        (await handler.Handle(new SetTaskTagsCommand(task.Id, [new string('x', 51)]), default)).Error.Should().Be("TASK_TAG_TOO_LONG");
    }

    // ── History, files ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_direct_assignment_is_in_the_history()
    {
        var task = await SeedTaskAsync(_me);

        await using (var db = _db.CreateContext())
        {
            var me = User(_me);
            var result = await new AssignTaskHandler(db, new AgentCapacityService(db), Substitute.For<IBus>(), Access(db, me), me)
                .Handle(new AssignTaskCommand(task.Id, _colleague), default);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var read = _db.CreateContext();
        var row = await read.TaskReassignments.SingleAsync();
        row.PreviousAgentId.Should().Be(_me);
        row.NewAgentId.Should().Be(_colleague);
        row.ActorId.Should().Be(_me);
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "contrat.pdf", "application/pdf")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14 }, "offre.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14 }, "archive.zip", null)]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, "outil.pdf", null)]
    [InlineData(new byte[] { 0x6E, 0x6F, 0x6D, 0x3B, 0x61 }, "liste.csv", "text/csv")]
    public void A_file_is_judged_by_its_bytes(byte[] head, string name, string? expected)
    {
        TaskAttachmentTypes.Detect(head, name).Should().Be(expected);
    }
}
