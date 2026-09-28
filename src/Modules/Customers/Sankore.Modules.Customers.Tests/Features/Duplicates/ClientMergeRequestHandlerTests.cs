namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.Merge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.ListClientMerges;
using Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class ClientMergeRequestHandlerTests : IDisposable
{
    private const string Reason = "Deux fiches pour la même personne, pièce identique.";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _otherAgencyId = Guid.NewGuid();
    private readonly Guid _requesterId = Guid.NewGuid();
    private readonly Guid _approverId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly RecordingEventPublisher _publisher = new();

    public ClientMergeRequestHandlerTests() => _factory = new TestCustomersDbContextFactory(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    // ── Request ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_opens_a_pending_request_and_links_the_workflow_instance()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var instanceId = Guid.NewGuid();
        var workflow = DuplicatesTestDoubles.WorkflowStarting(instanceId);

        var result = await RequestHandler(db, workflow).Handle(
            Request(survivor.Id, absorbed.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(MergeRequestStatus.PendingApproval),
            "nothing is merged until a second pair of eyes approves");
        result.Value.WorkflowInstanceId.Should().Be(instanceId);

        await using var verify = _factory.CreateContext();
        var stored = verify.ClientMergeRequests.IgnoreQueryFilters().Single();
        stored.WorkflowInstanceId.Should().Be(instanceId);
        stored.RequestedBy.Should().Be(_requesterId);

        // The clients themselves are untouched at this stage.
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == absorbed.Id)
            .Status.Should().Be(ClientStatus.PendingKyc);

        await workflow.Received(1).StartWorkflowAsync(
            Arg.Is<WorkflowStartRequest>(r =>
                r.EntityType == "ClientMerge"
                && r.EntityId == stored.Id
                && r.StartedByUserId == _requesterId
                && r.TenantId == _tenantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_survives_a_missing_workflow_template()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var result = await RequestHandler(db, DuplicatesTestDoubles.WorkflowWithoutTemplate()).Handle(
            Request(survivor.Id, absorbed.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("an unconfigured M12 must never disable the four-eyes control");
        result.Value.WorkflowInstanceId.Should().BeNull();

        await using var verify = _factory.CreateContext();
        verify.ClientMergeRequests.IgnoreQueryFilters().Single()
            .Status.Should().Be(MergeRequestStatus.PendingApproval);
    }

    [Fact]
    public async Task Request_survives_a_workflow_module_that_throws()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var result = await RequestHandler(db, DuplicatesTestDoubles.WorkflowThrowing()).Handle(
            Request(survivor.Id, absorbed.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WorkflowInstanceId.Should().BeNull();
    }

    [Fact]
    public async Task Request_refuses_to_merge_a_client_into_itself()
    {
        await using var db = _factory.CreateContext();
        var (survivor, _) = await SeedPairAsync(db);

        var result = await RequestHandler(db).Handle(
            Request(survivor.Id, survivor.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.SameClientMergeForbidden);
    }

    [Fact]
    public async Task Request_answers_not_found_for_an_unknown_client()
    {
        await using var db = _factory.CreateContext();
        var (survivor, _) = await SeedPairAsync(db);

        var result = await RequestHandler(db).Handle(
            Request(survivor.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Request_answers_not_found_when_a_client_is_outside_the_agency_perimeter()
    {
        await using var db = _factory.CreateContext();
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var outside = await TestClientFactory.SeedAsync(db,
            TestClientFactory.Individual(_tenantId, _otherAgencyId, clientNumber: "ABJ-2026-000002"));

        var result = await RequestHandler(db, scope: TestDoubles.AgencyScope(_agencyId)).Handle(
            Request(survivor.Id, outside.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound, "404, never 403");
    }

    [Fact]
    public async Task Request_refuses_a_client_that_is_already_merged()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var tracked = await db.Clients.AsTracking().IgnoreQueryFilters().FirstAsync(c => c.Id == absorbed.Id);
        tracked.MarkMerged(survivor.Id, _requesterId).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await RequestHandler(db).Handle(
            Request(survivor.Id, absorbed.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientAlreadyMerged);
    }

    [Fact]
    public async Task Request_validator_rejects_a_choice_that_is_neither_survivor_nor_absorbed()
    {
        var validator = new RequestClientMergeValidator();

        var bad = validator.Validate(new RequestClientMergeCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            new Dictionary<string, string> { [ClientMergeFields.LastName] = "whatever" },
            Reason));

        bad.IsValid.Should().BeFalse();

        var good = validator.Validate(new RequestClientMergeCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            new Dictionary<string, string> { [ClientMergeFields.LastName] = ClientMergeFields.Absorbed },
            Reason));

        good.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Request_validator_requires_a_substantial_reason()
    {
        var validator = new RequestClientMergeValidator();

        validator.Validate(new RequestClientMergeCommand(
            Guid.NewGuid(), Guid.NewGuid(), new Dictionary<string, string>(), "court"))
            .IsValid.Should().BeFalse();
    }

    // ── Approve ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_by_the_requester_is_forbidden()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var executor = Substitute.For<IClientMergeExecutor>();
        var result = await ApproveHandler(db, executor, actingAs: _requesterId).Handle(
            new ApproveClientMergeCommand(requestId, null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.SelfApprovalForbidden);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, default);

        await using var verify = _factory.CreateContext();
        verify.ClientMergeRequests.IgnoreQueryFilters().Single()
            .Status.Should().Be(MergeRequestStatus.PendingApproval);
    }

    [Fact]
    public async Task Approval_by_someone_else_executes_the_merge()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var result = await ApproveHandler(db, RealExecutor(db)).Handle(
            new ApproveClientMergeCommand(requestId, "Vérifié avec la pièce"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var stored = verify.ClientMergeRequests.IgnoreQueryFilters().Single();
        stored.Status.Should().Be(MergeRequestStatus.Executed);
        stored.DecidedBy.Should().Be(_approverId);
        stored.ExecutedAt.Should().NotBeNull();

        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == absorbed.Id)
            .MergedIntoId.Should().Be(survivor.Id);
    }

    [Fact]
    public async Task A_second_decision_is_refused()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        (await ApproveHandler(db, RealExecutor(db)).Handle(
            new ApproveClientMergeCommand(requestId, null), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        db.ChangeTracker.Clear();

        var again = await ApproveHandler(db, RealExecutor(db)).Handle(
            new ApproveClientMergeCommand(requestId, null), CancellationToken.None);

        again.Error.Should().Be(CustomerErrors.MergeAlreadyDecided);
    }

    [Fact]
    public async Task Approval_answers_not_found_for_an_unknown_request()
    {
        await using var db = _factory.CreateContext();

        var result = await ApproveHandler(db, Substitute.For<IClientMergeExecutor>()).Handle(
            new ApproveClientMergeCommand(Guid.NewGuid(), null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.MergeRequestNotFound);
    }

    [Fact]
    public async Task Approval_answers_not_found_when_a_client_is_outside_the_perimeter()
    {
        await using var db = _factory.CreateContext();
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var outside = await TestClientFactory.SeedAsync(db,
            TestClientFactory.Individual(_tenantId, _otherAgencyId, clientNumber: "ABJ-2026-000002"));
        var requestId = await OpenRequestAsync(db, survivor.Id, outside.Id);

        var executor = Substitute.For<IClientMergeExecutor>();
        var result = await ApproveHandler(db, executor, scope: TestDoubles.AgencyScope(_agencyId)).Handle(
            new ApproveClientMergeCommand(requestId, null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, default);
    }

    [Fact]
    public async Task Approval_in_another_tenant_cannot_see_the_request()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var foreignHandler = new ApproveClientMergeHandler(
            db,
            TestDoubles.CurrentUser(Guid.NewGuid(), _approverId),
            TestDoubles.AgencyScope(),
            Substitute.For<IClientMergeExecutor>(),
            TimeProvider.System);

        var result = await foreignHandler.Handle(
            new ApproveClientMergeCommand(requestId, null), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.MergeRequestNotFound);
    }

    // ── Reject ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rejection_closes_the_request_without_merging_anything()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var result = await RejectHandler(db).Handle(
            new RejectClientMergeCommand(requestId, "Ce sont deux frères"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var stored = verify.ClientMergeRequests.IgnoreQueryFilters().Single();
        stored.Status.Should().Be(MergeRequestStatus.Rejected);
        stored.DecidedBy.Should().Be(_approverId);
        stored.DecisionComment.Should().Be("Ce sont deux frères");

        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == absorbed.Id)
            .Status.Should().Be(ClientStatus.PendingKyc);
    }

    [Fact]
    public async Task Rejection_by_the_requester_is_forbidden_too()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var result = await RejectHandler(db, actingAs: _requesterId).Handle(
            new RejectClientMergeCommand(requestId, "Je me suis trompé"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.SelfApprovalForbidden);
    }

    [Fact]
    public async Task Rejection_requires_a_reason()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var result = await RejectHandler(db).Handle(
            new RejectClientMergeCommand(requestId, "  "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    // ── Read ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_returns_the_field_choices_and_both_client_labels()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var requestId = await OpenRequestAsync(db, survivor.Id, absorbed.Id, new Dictionary<string, string>
        {
            [ClientMergeFields.LastName] = ClientMergeFields.Absorbed,
        });

        var result = await new GetClientMergeHandler(
                db, TestDoubles.CurrentUser(_tenantId, _approverId), TestDoubles.AgencyScope())
            .Handle(new GetClientMergeQuery(requestId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SurvivorClientNumber.Should().Be("ABJ-2026-000001");
        result.Value.AbsorbedClientNumber.Should().Be("ABJ-2026-000002");
        result.Value.FieldChoices.Should().ContainKey(ClientMergeFields.LastName)
            .WhoseValue.Should().Be(ClientMergeFields.Absorbed);
    }

    [Fact]
    public async Task Detail_hides_a_request_whose_client_is_outside_the_perimeter()
    {
        await using var db = _factory.CreateContext();
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var outside = await TestClientFactory.SeedAsync(db,
            TestClientFactory.Individual(_tenantId, _otherAgencyId, clientNumber: "ABJ-2026-000002"));
        var requestId = await OpenRequestAsync(db, survivor.Id, outside.Id);

        var result = await new GetClientMergeHandler(
                db, TestDoubles.CurrentUser(_tenantId, _approverId), TestDoubles.AgencyScope(_agencyId))
            .Handle(new GetClientMergeQuery(requestId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.MergeRequestNotFound);
    }

    [Fact]
    public async Task List_pages_requests_and_filters_by_status()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var pending = await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var third = await TestClientFactory.SeedAsync(db,
            TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000003"));
        var decided = await OpenRequestAsync(db, survivor.Id, third.Id);
        await RejectHandler(db).Handle(
            new RejectClientMergeCommand(decided, "Pas la même personne"), CancellationToken.None);
        db.ChangeTracker.Clear();

        var handler = new ListClientMergesHandler(
            db, TestDoubles.CurrentUser(_tenantId, _approverId), TestDoubles.AgencyScope());

        var all = await handler.Handle(new ListClientMergesQuery(null), CancellationToken.None);
        all.Value.TotalCount.Should().Be(2);

        var onlyPending = await handler.Handle(
            new ListClientMergesQuery(MergeRequestStatus.PendingApproval), CancellationToken.None);

        onlyPending.Value.Items.Should().ContainSingle().Which.Id.Should().Be(pending);
    }

    [Fact]
    public async Task List_never_leaks_a_request_from_another_tenant()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        await OpenRequestAsync(db, survivor.Id, absorbed.Id);

        var handler = new ListClientMergesHandler(
            db, TestDoubles.CurrentUser(Guid.NewGuid(), _approverId), TestDoubles.AgencyScope());

        var result = await handler.Handle(new ListClientMergesQuery(null), CancellationToken.None);

        result.Value.Items.Should().BeEmpty();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static RequestClientMergeCommand Request(Guid survivorId, Guid absorbedId) =>
        new(survivorId, absorbedId, new Dictionary<string, string>(), Reason);

    private RequestClientMergeHandler RequestHandler(
        CustomersDbContext db, IWorkflowModule? workflow = null, IAgencyScopeProvider? scope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, _requesterId),
            scope ?? TestDoubles.AgencyScope(),
            workflow ?? DuplicatesTestDoubles.WorkflowStarting(Guid.NewGuid()),
            DuplicatesTestDoubles.Logger<RequestClientMergeHandler>());

    private ApproveClientMergeHandler ApproveHandler(
        CustomersDbContext db,
        IClientMergeExecutor executor,
        Guid? actingAs = null,
        IAgencyScopeProvider? scope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, actingAs ?? _approverId),
            scope ?? TestDoubles.AgencyScope(),
            executor,
            TimeProvider.System);

    private RejectClientMergeHandler RejectHandler(CustomersDbContext db, Guid? actingAs = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, actingAs ?? _approverId),
            TestDoubles.AgencyScope(),
            TimeProvider.System);

    private ClientMergeExecutor RealExecutor(CustomersDbContext db) =>
        new(db,
            _publisher,
            DuplicatesTestDoubles.PhoneticKeys,
            TimeProvider.System,
            DuplicatesTestDoubles.Logger<ClientMergeExecutor>());

    private async Task<(Client Survivor, Client Absorbed)> SeedPairAsync(CustomersDbContext db)
    {
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var absorbed = await TestClientFactory.SeedAsync(db,
            TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000002"));
        return (survivor, absorbed);
    }

    /// <summary>Opens a request through the real handler, so the fixture exercises the real path.</summary>
    private async Task<Guid> OpenRequestAsync(
        CustomersDbContext db,
        Guid survivorId,
        Guid absorbedId,
        Dictionary<string, string>? fieldChoices = null)
    {
        var result = await RequestHandler(db).Handle(
            new RequestClientMergeCommand(
                survivorId, absorbedId, fieldChoices ?? new Dictionary<string, string>(), Reason),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.ChangeTracker.Clear();
        return result.Value.MergeRequestId;
    }
}
