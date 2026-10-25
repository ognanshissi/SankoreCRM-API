namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Erasure;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Erasure on request (data-purge lot 2, owner's choices of 2026-10-24): one person records, ANOTHER
/// decides; a prospect is erased, a client under legal retention is restricted until it ends.
/// </summary>
public sealed class ErasureRequestsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _requester = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly FixedTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private const string Reason = "La personne demande l'effacement de ses données.";
    private const string Evidence = "Courrier reçu à l'agence le 20/10/2026, réf. EFF-0042.";

    public ErasureRequestsTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenantId);
        _kyc.IsRetentionClearedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _leads.CanActorWorkLeadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_prospect_is_erased_on_approval_by_a_second_person()
    {
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 1a2b3c4d", false, null));

        var request = await CreateAsync(ErasureSubjectType.Lead, leadId);
        request.SubjectReference.Should().Be("Lead 1a2b3c4d");

        var approved = await ApproveAsync(request.Id, _approver);

        approved.Value.Status.Should().Be(ErasureRequestStatus.Completed);
        await _leads.Received(1).EraseLeadAsync(_tenantId, leadId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_person_who_recorded_the_request_cannot_decide_it()
    {
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 1a2b3c4d", false, null));
        var request = await CreateAsync(ErasureSubjectType.Lead, leadId);

        (await ApproveAsync(request.Id, _requester)).Error.Should().Be(CustomerErrors.ErasureFourEyes);
        await _leads.DidNotReceiveWithAnyArgs().EraseLeadAsync(default, default, default);
    }

    [Fact]
    public async Task A_client_still_in_a_relationship_is_restricted_not_erased()
    {
        Client client;
        await using (var seed = _factory.CreateContext())
        {
            client = ComplianceTestData.Individual(_tenantId, _agencyId, "CLI-3001", "Awa", "Kone", _requester, _encryptor, _indexer);
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var request = await CreateAsync(ErasureSubjectType.Client, client.Id);
        var approved = await ApproveAsync(request.Id, _approver);

        approved.Value.Status.Should().Be(ErasureRequestStatus.Restricted);
        approved.Value.Blocker.Should().Be(CustomerErrors.ClientRelationshipActive);

        await using var reread = _factory.CreateContext();
        var stored = await reread.Clients.SingleAsync(c => c.Id == client.Id);
        stored.IsAnonymized.Should().BeFalse();
        stored.ProcessingRestrictedAt.Should().NotBeNull();
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync<ClientAnonymizedEvent>(default!, default);
    }

    [Fact]
    public async Task A_lead_of_a_client_follows_the_client_retention()
    {
        Client client;
        await using (var seed = _factory.CreateContext())
        {
            client = ComplianceTestData.ArchivedIndividual(_tenantId, _agencyId, "CLI-3002", _requester, _encryptor, _indexer);
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 9f8e7d6c", false, client.Id));

        // Archived today: ten years to go.
        var preview = await Eligibility().EvaluateAsync(_tenantId, ErasureSubjectType.Lead, leadId, default);

        preview.Eligible.Should().BeFalse();
        preview.Blocker.Should().Be(CustomerErrors.RetentionNotReached);
        preview.EligibleFrom.Should().BeCloseTo(DateTimeOffset.UtcNow.AddYears(10), TimeSpan.FromMinutes(5));
        preview.ClientId.Should().Be(client.Id);
    }

    [Fact]
    public async Task A_client_past_its_retention_is_anonymised_and_the_modules_are_told()
    {
        Client client;
        await using (var seed = _factory.CreateContext())
        {
            client = ComplianceTestData.ArchivedIndividual(_tenantId, _agencyId, "CLI-3003", _requester, _encryptor, _indexer);
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var request = await CreateAsync(ErasureSubjectType.Client, client.Id);
        _clock.Now = DateTimeOffset.UtcNow.AddYears(11);
        var approved = await ApproveAsync(request.Id, _approver);

        approved.Value.Status.Should().Be(ErasureRequestStatus.Completed);
        await using var reread = _factory.CreateContext();
        (await reread.Clients.SingleAsync(c => c.Id == client.Id)).IsAnonymized.Should().BeTrue();
        await _publisher.Received(1).PublishAsync(
            Arg.Is<ClientAnonymizedEvent>(e => e.ClientId == client.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_open_request_for_the_same_person_is_refused()
    {
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 1a2b3c4d", false, null));
        await CreateAsync(ErasureSubjectType.Lead, leadId);

        await using var db = _factory.CreateContext();
        var second = await CreateHandler(db).Handle(
            new CreateErasureRequestCommand(ErasureSubjectType.Lead, leadId, Reason, Evidence), default);

        second.Error.Should().Be(CustomerErrors.ErasureAlreadyRequested);
    }

    [Fact]
    public async Task A_rejected_request_erases_nothing()
    {
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 1a2b3c4d", false, null));
        var request = await CreateAsync(ErasureSubjectType.Lead, leadId);

        await using var db = _factory.CreateContext();
        var rejected = await new RejectErasureRequestHandler(db, TestDoubles.CurrentUser(_tenantId, _approver, "RegulationManager"), _clock)
            .Handle(new RejectErasureRequestCommand(request.Id, "Obligation légale de conservation en cours."), default);

        rejected.Value.Status.Should().Be(ErasureRequestStatus.Rejected);
        await _leads.DidNotReceiveWithAnyArgs().EraseLeadAsync(default, default, default);
    }

    [Fact]
    public async Task Only_the_deciders_read_every_request_the_others_read_their_own()
    {
        var leadId = Guid.NewGuid();
        _leads.GetErasureSubjectAsync(_tenantId, leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadErasureSubject(leadId, "Lead 1a2b3c4d", false, null));
        await CreateAsync(ErasureSubjectType.Lead, leadId);

        async Task<int> CountFor(Guid userId, params string[] permissions)
        {
            var user = Substitute.For<Sankore.Shared.Infrastructure.Auth.ICurrentUser>();
            user.TenantId.Returns(_tenantId);
            user.Id.Returns(userId);
            user.Permissions.Returns(permissions);
            await using var db = _factory.CreateContext();
            return (await new ListErasureRequestsHandler(db, user).Handle(new ListErasureRequestsQuery(null), default))
                .Value.TotalCount;
        }

        (await CountFor(_requester)).Should().Be(1);
        (await CountFor(Guid.NewGuid())).Should().Be(0, "another agency's manager has no business reading it");
        (await CountFor(_approver, Permissions.CanApproveErasure.Code)).Should().Be(1);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private ErasureEligibility Eligibility(CustomersDbContext? db = null) =>
        new(db ?? _factory.CreateContext(), TestDoubles.Settings(_tenantId, (CustomerSettingKeys.RetentionYears, "10")), _kyc, _clock, _leads);

    private CreateErasureRequestHandler CreateHandler(CustomersDbContext db)
    {
        var user = TestDoubles.CurrentUser(_tenantId, _requester, "SalesManager");
        return new(db, user, new ErasurePerimeter(user, TestDoubles.AgencyScope(), db, _leads), Eligibility(db), _clock);
    }

    private async Task<ErasureRequestDto> CreateAsync(ErasureSubjectType type, Guid subjectId)
    {
        await using var db = _factory.CreateContext();
        var created = await CreateHandler(db).Handle(new CreateErasureRequestCommand(type, subjectId, Reason, Evidence), default);
        created.IsSuccess.Should().BeTrue(created.Error);
        return created.Value;
    }

    private async Task<Result<ErasureRequestDto>> ApproveAsync(Guid requestId, Guid actor)
    {
        await using var db = _factory.CreateContext();
        var executor = new ErasureExecutor(db, _publisher, _clock, NullLogger<ErasureExecutor>.Instance, _leads);
        return await new ApproveErasureRequestHandler(
                db, TestDoubles.CurrentUser(_tenantId, actor, "RegulationManager"), Eligibility(db), executor, _clock)
            .Handle(new ApproveErasureRequestCommand(requestId, null), default);
    }
}
