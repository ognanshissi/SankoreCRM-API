namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

public sealed class DetectDuplicatesHandlerTests : IDisposable
{
    private const string SharedDateOfBirthIndex = "dob-blind-index-1987-03-04";

    /// <summary>
    /// The same national id card on both records. Weight 60 on its own, so a twin pair built with it
    /// clears the default threshold of 70 comfortably instead of sitting exactly on it — the test
    /// asserts the detection rule, not the arithmetic of the scorer's weights.
    /// </summary>
    private const string SharedDocumentNumber = "CI0012345678";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public DetectDuplicatesHandlerTests() => _factory = new TestCustomersDbContextFactory(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    private DetectDuplicatesHandler Handler(CustomersDbContext db, int threshold = 70) =>
        new(db,
            TestDoubles.Settings(_tenantId,
                (CustomerSettingKeys.DuplicateScoreThreshold, threshold.ToString(CultureInfo.InvariantCulture))),
            TimeProvider.System,
            DuplicatesTestDoubles.Logger<DetectDuplicatesHandler>());

    [Fact]
    public async Task Two_clients_sharing_names_and_birth_date_are_flagged_as_a_candidate()
    {
        await using var db = _factory.CreateContext();
        var first = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        var second = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CandidatesCreated.Should().Be(1);

        await using var verify = _factory.CreateContext();
        var candidate = verify.DuplicateCandidates.IgnoreQueryFilters().Single();
        var (a, b) = DuplicatesTestDoubles.Canonical(first.Id, second.Id);

        candidate.ClientAId.Should().Be(a, "the pair key is canonical: ClientAId < ClientBId");
        candidate.ClientBId.Should().Be(b);
        candidate.Status.Should().Be(DuplicateCandidateStatus.ToReview);
        candidate.Score.Should().BeGreaterThanOrEqualTo(70);
        candidate.ReasonsJson.Should().Contain("phonetic-both").And.Contain("date-of-birth");
    }

    [Fact]
    public async Task Pair_below_the_tenant_threshold_is_ignored()
    {
        await using var db = _factory.CreateContext();
        // Same birth date (20) and same agency (5) only: 25, far below the threshold.
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001");
        await SeedTwinAsync(db, "Mariam", "Traoré", "ABJ-2026-000002");

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.Value.CandidatesCreated.Should().Be(0);

        await using var verify = _factory.CreateContext();
        verify.DuplicateCandidates.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task Pair_with_no_blocking_key_in_common_is_never_compared()
    {
        await using var db = _factory.CreateContext();

        // Different names, different birth dates, no document: nothing to block on, so the pair is
        // not even scored — which is the whole point of blocking.
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", dateOfBirthIndex: "dob-a");
        await SeedTwinAsync(db, "Mariam", "Traoré", "ABJ-2026-000002", dateOfBirthIndex: "dob-b");

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.Value.PairsCompared.Should().Be(0);
        result.Value.CandidatesCreated.Should().Be(0);
    }

    [Fact]
    public async Task Existing_pair_to_review_is_refreshed_not_duplicated()
    {
        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);
        db.ChangeTracker.Clear();

        var second = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        second.Value.CandidatesCreated.Should().Be(0);
        second.Value.CandidatesRefreshed.Should().Be(1);

        await using var verify = _factory.CreateContext();
        verify.DuplicateCandidates.IgnoreQueryFilters().Should().HaveCount(1);
    }

    [Fact]
    public async Task Rejected_pair_is_not_proposed_again_while_the_compared_data_is_unchanged()
    {
        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);
        db.ChangeTracker.Clear();

        var candidate = await db.DuplicateCandidates.AsTracking().IgnoreQueryFilters().SingleAsync();
        candidate.Reject(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rerun = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        rerun.Value.CandidatesSkipped.Should().Be(1);
        rerun.Value.CandidatesRefreshed.Should().Be(0);

        await using var verify = _factory.CreateContext();
        verify.DuplicateCandidates.IgnoreQueryFilters().Single()
            .Status.Should().Be(DuplicateCandidateStatus.Rejected);
    }

    [Fact]
    public async Task Rejected_pair_comes_back_to_review_once_a_compared_field_changes()
    {
        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        var second = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);
        db.ChangeTracker.Clear();

        var candidate = await db.DuplicateCandidates.AsTracking().IgnoreQueryFilters().SingleAsync();
        candidate.Reject(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A compared field moves: the fingerprint changes, so the earlier rejection no longer
        // describes this comparison.
        var tracked = await db.Clients.AsTracking().IgnoreQueryFilters()
            .FirstAsync(c => c.Id == second.Id);
        DuplicatesTestDoubles.OverrideParents(db, tracked, fatherName: "Seydou Ouattara", motherName: null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rerun = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        rerun.Value.CandidatesRefreshed.Should().Be(1);

        await using var verify = _factory.CreateContext();
        var refreshed = verify.DuplicateCandidates.IgnoreQueryFilters().Single();
        refreshed.Status.Should().Be(DuplicateCandidateStatus.ToReview);
        refreshed.ReviewedBy.Should().BeNull();
    }

    [Fact]
    public async Task Merged_pair_is_ignored_for_good()
    {
        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);
        db.ChangeTracker.Clear();

        var candidate = await db.DuplicateCandidates.AsTracking().IgnoreQueryFilters().SingleAsync();
        candidate.MarkMerged(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rerun = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        rerun.Value.CandidatesSkipped.Should().Be(1);
        rerun.Value.CandidatesRefreshed.Should().Be(0);
    }

    [Fact]
    public async Task Archived_and_merged_clients_are_left_out_of_the_scan()
    {
        await using var db = _factory.CreateContext();
        var survivor = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        var absorbed = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        var tracked = await db.Clients.AsTracking().IgnoreQueryFilters().FirstAsync(c => c.Id == absorbed.Id);
        tracked.MarkMerged(survivor.Id, Guid.NewGuid()).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.Value.ClientsExamined.Should().Be(1);
        result.Value.CandidatesCreated.Should().Be(0);
    }

    [Fact]
    public async Task Detection_never_decrypts_a_protected_field()
    {
        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000002", documentNumber: SharedDocumentNumber);

        // The handler is built through DI with a watched IFieldEncryptor in the container: if a
        // future refactor ever injects decryption into the detection path, this test fails.
        var encryptor = Substitute.For<IFieldEncryptor>();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(encryptor);
        services.AddSingleton(TestDoubles.Settings(_tenantId));
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        var handler = ActivatorUtilities.CreateInstance<DetectDuplicatesHandler>(provider);
        var result = await handler.Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CandidatesCreated.Should().Be(1, "an identical document blind index is a strong signal");

        encryptor.DidNotReceive().Decrypt(Arg.Any<string?>());
        encryptor.ReceivedCalls().Should().BeEmpty("scoring compares blind indexes, never clear values");
    }

    [Fact]
    public async Task A_client_of_another_tenant_is_never_paired()
    {
        var otherTenantId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000001", documentNumber: SharedDocumentNumber);

        // Byte-for-byte the same person, same document, filed under a different tenant.
        var foreign = TestClientFactory.Individual(
            otherTenantId, _agencyId, first: "Awa", last: "Ouattara", clientNumber: "ABJ-2026-000009",
            identityDocumentNumber: SharedDocumentNumber);
        foreign.SetPhoneticKeys(
            DuplicatesTestDoubles.PhoneticKeys.Compute("Ouattara"),
            DuplicatesTestDoubles.PhoneticKeys.Compute("Awa"));
        DuplicatesTestDoubles.OverrideDateOfBirthBlindIndex(db, foreign, SharedDateOfBirthIndex);
        await TestClientFactory.SeedAsync(db, foreign);

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.Value.ClientsExamined.Should().Be(1);
        result.Value.CandidatesCreated.Should().Be(0);
    }

    [Fact]
    public async Task A_client_whose_two_phonetic_keys_are_identical_does_not_abort_the_run()
    {
        // Regression. "Kouassi Kouassi" folds to ONE phonetic key for both the given and the
        // family name, and BuildBlocks files both keys under the same "PH:" namespace — so this
        // client used to be appended to that block twice. The pair loop then read two positions
        // holding the same id and asked DuplicateCandidate.Detect to pair the client with itself,
        // which throws DomainException "A client cannot duplicate itself." A single such client
        // took down the whole tenant's detection run, including every unrelated pair in it.
        await using var db = _factory.CreateContext();

        // Its OWN document number, not the shared one: the self-pair must clear the threshold on
        // its own signals without also matching the unrelated pair below. Both halves of the
        // premise matter — identical keys put the client in one block twice, and the identity
        // document (weight 60, on top of 25 both-phonetic + 20 birth date + 5 agency) is what
        // carries the self-comparison past the threshold of 70. Without a document the self-score
        // is 50, the pair is dropped before Detect, and the bug stays invisible.
        var selfKeyed = await SeedTwinAsync(
            db, "Kouassi", "Kouassi", "ABJ-2026-000010", documentNumber: "CI0099999999");
        selfKeyed.PhoneticKeyPrimary.Should().Be(
            selfKeyed.PhoneticKeySecondary,
            "the premise of this test: both names must fold to the same key");
        selfKeyed.IdentityDocumentNumberBlindIndex.Should().NotBeNull(
            "the other half of the premise: the self-comparison must reach the threshold");

        // A real pair in the same run, to prove the run completes rather than merely not throwing.
        var first = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000011", documentNumber: SharedDocumentNumber);
        var second = await SeedTwinAsync(db, "Awa", "Ouattara", "ABJ-2026-000012", documentNumber: SharedDocumentNumber);

        var result = await Handler(db).Handle(new DetectDuplicatesCommand(_tenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Load-bearing, and not redundant with IsSuccess. The per-pair try/catch in the handler
        // would ALSO keep this run succeeding if the block dedupe were removed — it would just
        // swallow the self-pair and carry on. Asserting the counter is what keeps this test honest
        // about the fix it exists for: the self-pair must never be formed, not merely survived.
        result.Value.CandidatesFailed.Should().Be(
            0, "the self-pair must never reach the aggregate, not be rescued by the per-pair catch");

        await using var verify = _factory.CreateContext();
        var candidates = verify.DuplicateCandidates.IgnoreQueryFilters().ToList();

        candidates.Should().NotContain(
            c => c.ClientAId == c.ClientBId,
            "no candidate may pair a client with itself");
        candidates.Should().NotContain(
            c => c.ClientAId == selfKeyed.Id || c.ClientBId == selfKeyed.Id,
            "the self-keyed client matches nobody else here, so it yields no candidate at all");

        var (a, b) = DuplicatesTestDoubles.Canonical(first.Id, second.Id);
        candidates.Should().ContainSingle(c => c.ClientAId == a && c.ClientBId == b,
            "the unrelated pair in the same run must still be detected");
    }

    private async Task<Client> SeedTwinAsync(
        CustomersDbContext db,
        string first,
        string last,
        string clientNumber,
        string? dateOfBirthIndex = null,
        string? documentNumber = null)
        => await DuplicatesTestDoubles.SeedDetectableAsync(
            db, _tenantId, _agencyId, first, last, clientNumber,
            dateOfBirthBlindIndex: dateOfBirthIndex ?? SharedDateOfBirthIndex,
            identityDocumentNumber: documentNumber);
}
