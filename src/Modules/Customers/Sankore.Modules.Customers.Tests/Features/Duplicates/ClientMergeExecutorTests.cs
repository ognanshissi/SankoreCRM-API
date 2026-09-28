namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.Merge;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class ClientMergeExecutorTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly RecordingEventPublisher _publisher = new();
    private readonly IFieldEncryptor _encryptor = TestDoubles.Encryptor();
    private readonly IBlindIndexer _indexer = TestDoubles.Indexer();

    public ClientMergeExecutorTests() => _factory = new TestCustomersDbContextFactory(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    private ClientMergeExecutor Executor(CustomersDbContext db) =>
        new(db,
            _publisher,
            DuplicatesTestDoubles.PhoneticKeys,
            TimeProvider.System,
            DuplicatesTestDoubles.Logger<ClientMergeExecutor>());

    [Fact]
    public async Task Execution_marks_the_absorbed_client_merged_and_points_it_at_the_survivor()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        var result = await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var stored = verify.Clients.IgnoreQueryFilters().Single(c => c.Id == absorbed.Id);
        stored.Status.Should().Be(ClientStatus.Merged);
        stored.MergedIntoId.Should().Be(survivor.Id);
        stored.IsReadOnly.Should().BeTrue();

        verify.ClientMergeRequests.IgnoreQueryFilters().Single(r => r.Id == request.Id)
            .Status.Should().Be(MergeRequestStatus.Executed);
    }

    [Fact]
    public async Task Execution_publishes_ClientsMergedEvent()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);

        _publisher.OfType<ClientsMergedEvent>().Should().ContainSingle(e =>
            e.TenantId == _tenantId
            && e.SurvivorClientId == survivor.Id
            && e.AbsorbedClientId == absorbed.Id
            && e.ActorUserId == _actorId);
    }

    [Fact]
    public async Task Active_contact_points_follow_the_survivor_without_a_second_primary_of_a_type()
    {
        await using var db = _factory.CreateContext();
        var survivor = TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var absorbed = TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000002");

        AddPhone(survivor, "+225 07 00 00 01");
        AddPhone(absorbed, "+225 07 00 00 02");
        var closedOnAbsorbed = AddPhone(absorbed, "+225 07 00 00 03");
        absorbed.CloseContactPoint(closedOnAbsorbed.Id, _actorId, DateTimeOffset.UtcNow);

        await TestClientFactory.SeedAsync(db, survivor);
        await TestClientFactory.SeedAsync(db, absorbed);
        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var moved = verify.ClientContactPoints.IgnoreQueryFilters()
            .Where(cp => cp.ClientId == survivor.Id)
            .ToList();

        moved.Should().HaveCount(2, "only the absorbed client's ACTIVE contact points follow");
        moved.Count(cp => cp.IsPrimary && cp.Type == ContactPointType.Phone)
            .Should().Be(1, "the survivor keeps its own primary phone");

        verify.ClientContactPoints.IgnoreQueryFilters()
            .Should().Contain(cp => cp.Id == closedOnAbsorbed.Id && cp.ClientId == absorbed.Id);
    }

    [Fact]
    public async Task Relationships_memberships_beneficial_owners_and_timeline_follow_the_survivor()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var thirdParty = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, first: "Ibrahim", last: "Bamba", clientNumber: "ABJ-2026-000003");

        var relationship = ClientRelationship.ToClient(
            _tenantId, absorbed.Id, RelationshipType.Guarantor, thirdParty.Id, DateTimeOffset.UtcNow, _actorId);
        db.ClientRelationships.Add(relationship);

        var inbound = ClientRelationship.ToClient(
            _tenantId, thirdParty.Id, RelationshipType.Proxy, absorbed.Id, DateTimeOffset.UtcNow, _actorId);
        db.ClientRelationships.Add(inbound);

        var group = TestClientFactory.Group(_tenantId, _agencyId);
        group.AddMember(absorbed.Id, GroupOfficeRole.Member, DateTimeOffset.UtcNow, _actorId);
        db.ClientGroups.Add(group);

        var legal = TestClientFactory.Legal(_tenantId, _agencyId, clientNumber: "ABJ-2026-000004");
        db.Clients.Add(legal);
        var owner = BeneficialOwner.ForClient(
            _tenantId, legal.Id, absorbed.Id, 40m, ControlType.Ownership, DateTimeOffset.UtcNow, _actorId);
        db.BeneficialOwners.Add(owner);

        var entry = ClientTimelineEntry.Create(
            _tenantId, absorbed.Id, "Customers", "CLIENT_CREATED", DateTimeOffset.UtcNow,
            "Client créé", null, null, $"Customers:CLIENT_CREATED:{absorbed.Id}");
        db.ClientTimelineEntries.Add(entry);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);
        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();

        verify.ClientRelationships.IgnoreQueryFilters().Single(r => r.Id == relationship.Id)
            .ClientId.Should().Be(survivor.Id);
        verify.ClientRelationships.IgnoreQueryFilters().Single(r => r.Id == inbound.Id)
            .RelatedClientId.Should().Be(survivor.Id, "links pointing AT the absorbed client are repointed too");
        verify.GroupMemberships.IgnoreQueryFilters().Single()
            .ClientId.Should().Be(survivor.Id);
        verify.BeneficialOwners.IgnoreQueryFilters().Single()
            .LinkedClientId.Should().Be(survivor.Id);
        verify.ClientTimelineEntries.IgnoreQueryFilters().Single()
            .ClientId.Should().Be(survivor.Id);
    }

    [Fact]
    public async Task A_link_between_the_two_merged_clients_is_closed_instead_of_becoming_a_self_link()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var betweenTheTwo = ClientRelationship.ToClient(
            _tenantId, absorbed.Id, RelationshipType.Sibling, survivor.Id, DateTimeOffset.UtcNow, _actorId);
        db.ClientRelationships.Add(betweenTheTwo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);
        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var stored = verify.ClientRelationships.IgnoreQueryFilters().Single();
        stored.IsActive.Should().BeFalse("a client cannot be related to itself");
        stored.CloseReason.Should().Be("MERGE_SELF_LINK");
    }

    [Fact]
    public async Task A_membership_the_survivor_already_holds_is_closed_rather_than_duplicated()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var group = TestClientFactory.Group(_tenantId, _agencyId);
        group.AddMember(survivor.Id, GroupOfficeRole.President, DateTimeOffset.UtcNow, _actorId);
        group.AddMember(absorbed.Id, GroupOfficeRole.Member, DateTimeOffset.UtcNow, _actorId);
        db.ClientGroups.Add(group);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);
        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var memberships = verify.GroupMemberships.IgnoreQueryFilters().ToList();

        memberships.Where(m => m.LeftAt == null).Should().ContainSingle()
            .Which.ClientId.Should().Be(survivor.Id);
        memberships.Single(m => m.LeftAt != null).LeaveReason.Should().Be("MERGE_DUPLICATE_MEMBERSHIP");
    }

    [Fact]
    public async Task Field_choices_take_the_absorbed_value_and_ignore_unknown_fields()
    {
        await using var db = _factory.CreateContext();
        var survivor = TestClientFactory.Individual(
            _tenantId, _agencyId, first: "Awa", last: "Ouattara", clientNumber: "ABJ-2026-000001");
        var absorbed = TestClientFactory.Individual(
            _tenantId, _agencyId, first: "Awa", last: "Kouassi", clientNumber: "ABJ-2026-000002",
            identityDocumentNumber: "CI0099887766");

        await TestClientFactory.SeedAsync(db, survivor);
        await TestClientFactory.SeedAsync(db, absorbed);

        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id, new Dictionary<string, string>
        {
            [ClientMergeFields.LastName] = ClientMergeFields.Absorbed,
            [ClientMergeFields.IdentityDocument] = ClientMergeFields.Absorbed,
            [ClientMergeFields.FirstName] = ClientMergeFields.Survivor,
            ["ThisFieldDoesNotExist"] = ClientMergeFields.Absorbed,
        });

        var result = await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        result.IsSuccess.Should().BeTrue("an unknown field name is dropped silently, never fatal");
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var stored = verify.Clients.IgnoreQueryFilters().Single(c => c.Id == survivor.Id);

        stored.LastName.Should().Be("Kouassi");
        stored.FirstName.Should().Be("Awa");
        stored.EncryptedIdentityDocumentNumber.Should().Be(_encryptor.Encrypt("CI0099887766"));
        stored.IdentityDocumentNumberBlindIndex.Should().Be(
            _indexer.Compute(BlindIndexPurpose.IdentityDocument, "CI0099887766"),
            "the ciphertext and its blind index always move together");

        // The name changed, so the survivor must be findable by the nightly detection under its new key.
        stored.PhoneticKeyPrimary.Should().Be(DuplicatesTestDoubles.PhoneticKeys.Compute("Kouassi"));
    }

    [Fact]
    public async Task Diverging_KYC_statuses_reopen_the_KYC_review()
    {
        await using var db = _factory.CreateContext();
        var survivor = TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var absorbed = TestClientFactory.Individual(_tenantId, _agencyId, clientNumber: "ABJ-2026-000002");

        // Only one of the two was validated: the surviving file now mixes both verdicts.
        absorbed.ApplyKycValidated(DateTimeOffset.UtcNow, _actorId).IsSuccess.Should().BeTrue();
        absorbed.KycStatus.Should().Be(KycStatus.Approved);

        await TestClientFactory.SeedAsync(db, survivor);
        await TestClientFactory.SeedAsync(db, absorbed);
        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);

        _publisher.OfType<ClientSensitiveDataChangedEvent>().Should().ContainSingle(e =>
            e.ClientId == survivor.Id
            && e.Reason == "MERGE_KYC_REVIEW"
            && e.ChangedFields.Count == 1
            && e.ChangedFields[0] == nameof(Client.KycStatus));
    }

    [Fact]
    public async Task Identical_KYC_statuses_do_not_reopen_the_review()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);

        _publisher.OfType<ClientSensitiveDataChangedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Execution_closes_the_duplicate_candidate_of_the_pair()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);

        var (a, b) = DuplicatesTestDoubles.Canonical(survivor.Id, absorbed.Id);
        db.DuplicateCandidates.Add(DuplicateCandidate.Detect(
            _tenantId, a, b, 95, "[]", "fp-a", "fp-b", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var request = await SeedRequestAsync(db, survivor.Id, absorbed.Id);
        await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = _factory.CreateContext();
        var candidate = verify.DuplicateCandidates.IgnoreQueryFilters().Single();
        candidate.Status.Should().Be(DuplicateCandidateStatus.Merged);
        candidate.ReviewedBy.Should().Be(_actorId);
    }

    [Fact]
    public async Task An_already_merged_client_cannot_be_absorbed_twice()
    {
        await using var db = _factory.CreateContext();
        var (survivor, absorbed) = await SeedPairAsync(db);
        var first = await SeedRequestAsync(db, survivor.Id, absorbed.Id);

        await Executor(db).ExecuteAsync(first, _actorId, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var second = await SeedRequestAsync(db, survivor.Id, absorbed.Id);
        var result = await Executor(db).ExecuteAsync(second, _actorId, CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientAlreadyMerged);
    }

    [Fact]
    public async Task A_three_level_merge_chain_resolves_to_the_final_survivor()
    {
        await using var db = _factory.CreateContext();
        var first = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var middle = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000002");
        var last = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000003");

        // first → middle, then middle → last.
        var firstRequest = await SeedRequestAsync(db, middle.Id, first.Id);
        (await Executor(db).ExecuteAsync(firstRequest, _actorId, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var secondRequest = await SeedRequestAsync(db, last.Id, middle.Id);
        (await Executor(db).ExecuteAsync(secondRequest, _actorId, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await using var verify = _factory.CreateContext();
        var facade = new CustomersModuleFacade(verify, new UnusedLeadConversionService());

        (await facade.ResolveClientIdAsync(_tenantId, first.Id, CancellationToken.None))
            .Should().Be(last.Id, "a reference to the oldest record must land on the surviving one");
        (await facade.ResolveClientIdAsync(_tenantId, middle.Id, CancellationToken.None))
            .Should().Be(last.Id);
        (await facade.ResolveClientIdAsync(_tenantId, last.Id, CancellationToken.None))
            .Should().Be(last.Id);
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_never_reachable_by_a_merge()
    {
        var otherTenantId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");

        var foreign = TestClientFactory.Individual(
            otherTenantId, _agencyId, clientNumber: "ABJ-2026-000009");
        await TestClientFactory.SeedAsync(db, foreign);

        // The request itself belongs to this tenant, but names a client of another one.
        var request = await SeedRequestAsync(db, survivor.Id, foreign.Id);
        var result = await Executor(db).ExecuteAsync(request, _actorId, CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);

        await using var verify = _factory.CreateContext();
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == foreign.Id)
            .Status.Should().Be(ClientStatus.PendingKyc);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private async Task<(Client Survivor, Client Absorbed)> SeedPairAsync(CustomersDbContext db)
    {
        var survivor = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000001");
        var absorbed = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: "ABJ-2026-000002");
        return (survivor, absorbed);
    }

    private async Task<ClientMergeRequest> SeedRequestAsync(
        CustomersDbContext db,
        Guid survivorId,
        Guid absorbedId,
        Dictionary<string, string>? fieldChoices = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            fieldChoices ?? new Dictionary<string, string>());

        var request = ClientMergeRequest.Open(_tenantId, survivorId, absorbedId, json, Guid.NewGuid());
        var approved = request.Approve(_actorId, "Same person", DateTimeOffset.UtcNow);
        approved.IsSuccess.Should().BeTrue();

        db.ClientMergeRequests.Add(request);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Returned tracked by the context the executor will use, exactly as the approve handler does.
        return await db.ClientMergeRequests.AsTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == request.Id);
    }

    private ClientContactPoint AddPhone(Client client, string phone) =>
        client.AddContactPoint(
            ContactPointType.Phone,
            _encryptor.Encrypt(phone)!,
            _indexer.Compute(BlindIndexPurpose.Phone, phone),
            label: null,
            isPrimary: false,
            validFrom: DateTimeOffset.UtcNow,
            actor: Guid.NewGuid());
}
