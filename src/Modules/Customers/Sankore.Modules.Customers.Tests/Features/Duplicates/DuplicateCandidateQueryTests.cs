namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates;
using Sankore.Modules.Customers.Features.Duplicates.ListDuplicateCandidates;
using Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class DuplicateCandidateQueryTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _otherAgencyId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public DuplicateCandidateQueryTests() => _factory = new TestCustomersDbContextFactory(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    private ListDuplicateCandidatesHandler ListHandler(
        CustomersDbContext db, IAgencyScopeProvider? scope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, _userId),
            scope ?? TestDoubles.AgencyScope());

    private RejectDuplicateCandidateHandler RejectHandler(
        CustomersDbContext db, IAgencyScopeProvider? scope = null) =>
        new(db,
            TestDoubles.CurrentUser(_tenantId, _userId),
            scope ?? TestDoubles.AgencyScope(),
            TimeProvider.System);

    [Fact]
    public async Task List_returns_both_client_summaries_and_the_match_reasons()
    {
        await using var db = _factory.CreateContext();
        var (a, b) = await SeedCandidateAsync(db, score: 88);

        var result = await ListHandler(db).Handle(
            new ListDuplicateCandidatesQuery(null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Should().ContainSingle().Subject;

        item.Score.Should().Be(88);
        item.Status.Should().Be(nameof(DuplicateCandidateStatus.ToReview));
        item.Reasons.Should().ContainSingle(r => r.Key == "phonetic-both" && r.Weight == 25);
        new[] { item.ClientA.ClientId, item.ClientB.ClientId }.Should().BeEquivalentTo([a.Id, b.Id]);
        item.ClientA.ClientNumber.Should().NotBeEmpty();
    }

    [Fact]
    public async Task List_sorts_by_score_descending_and_filters_by_status()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 71, suffix: "1");
        await SeedCandidateAsync(db, score: 96, suffix: "2");
        var rejected = await SeedCandidateAsync(db, score: 80, suffix: "3", status: DuplicateCandidateStatus.Rejected);

        var all = await ListHandler(db).Handle(new ListDuplicateCandidatesQuery(null), CancellationToken.None);
        all.Value.Items.Select(i => i.Score).Should().ContainInOrder(96, 80, 71);

        var onlyRejected = await ListHandler(db).Handle(
            new ListDuplicateCandidatesQuery(DuplicateCandidateStatus.Rejected), CancellationToken.None);

        var onlyItem = onlyRejected.Value.Items.Should().ContainSingle().Subject;
        new[] { onlyItem.ClientA.ClientId, onlyItem.ClientB.ClientId }
            .Should().BeEquivalentTo([rejected.A.Id, rejected.B.Id]);
    }

    [Fact]
    public async Task List_pages_and_never_asks_for_a_negative_offset()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90, suffix: "1");
        await SeedCandidateAsync(db, score: 80, suffix: "2");

        var result = await ListHandler(db).Handle(
            new ListDuplicateCandidatesQuery(null, Page: 0, PageSize: 1), CancellationToken.None);

        result.Value.Page.Should().Be(1, "page 0 is clamped, not turned into a negative Skip");
        result.Value.Items.Should().HaveCount(1);
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task List_hides_a_pair_whose_second_client_is_outside_the_agency_perimeter()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90, secondClientAgencyId: _otherAgencyId);

        var restricted = await ListHandler(db, TestDoubles.AgencyScope(_agencyId)).Handle(
            new ListDuplicateCandidatesQuery(null), CancellationToken.None);

        restricted.Value.Items.Should().BeEmpty("a merge needs both records, so half a pair is not listed");

        var superUser = await ListHandler(db).Handle(
            new ListDuplicateCandidatesQuery(null), CancellationToken.None);

        superUser.Value.Items.Should().HaveCount(1, "an unrestricted perimeter applies no filter");
    }

    [Fact]
    public async Task List_never_shows_a_candidate_of_another_tenant()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90);

        var otherTenant = new ListDuplicateCandidatesHandler(
            db, TestDoubles.CurrentUser(Guid.NewGuid(), _userId), TestDoubles.AgencyScope());

        var result = await otherTenant.Handle(new ListDuplicateCandidatesQuery(null), CancellationToken.None);

        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Reject_marks_the_candidate_rejected_with_its_reviewer()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90);
        var candidateId = db.DuplicateCandidates.IgnoreQueryFilters().Single().Id;

        var result = await RejectHandler(db).Handle(
            new RejectDuplicateCandidateCommand(candidateId, "Deux sœurs, pièces différentes"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var stored = verify.DuplicateCandidates.IgnoreQueryFilters().Single();
        stored.Status.Should().Be(DuplicateCandidateStatus.Rejected);
        stored.ReviewedBy.Should().Be(_userId);
        stored.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reject_requires_a_reason()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90);
        var candidateId = db.DuplicateCandidates.IgnoreQueryFilters().Single().Id;

        var result = await RejectHandler(db).Handle(
            new RejectDuplicateCandidateCommand(candidateId, "   "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Reject_answers_not_found_for_an_unknown_candidate()
    {
        await using var db = _factory.CreateContext();

        var result = await RejectHandler(db).Handle(
            new RejectDuplicateCandidateCommand(Guid.NewGuid(), "Pas le même client"), CancellationToken.None);

        result.Error.Should().Be(DuplicatesErrors.DuplicateCandidateNotFound);
    }

    [Fact]
    public async Task Reject_answers_not_found_when_one_client_is_outside_the_perimeter()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90, secondClientAgencyId: _otherAgencyId);
        var candidateId = db.DuplicateCandidates.IgnoreQueryFilters().Single().Id;

        var result = await RejectHandler(db, TestDoubles.AgencyScope(_agencyId)).Handle(
            new RejectDuplicateCandidateCommand(candidateId, "Pas le même client"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound, "404, never 403: existence stays hidden");
    }

    [Fact]
    public async Task Reject_refuses_a_pair_that_was_already_merged()
    {
        await using var db = _factory.CreateContext();
        await SeedCandidateAsync(db, score: 90, status: DuplicateCandidateStatus.Merged);
        var candidateId = db.DuplicateCandidates.IgnoreQueryFilters().Single().Id;

        var result = await RejectHandler(db).Handle(
            new RejectDuplicateCandidateCommand(candidateId, "Trop tard"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientAlreadyMerged);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private const string Reasons = """[{"Key":"phonetic-both","Weight":25}]""";

    private async Task<(Client A, Client B)> SeedCandidateAsync(
        CustomersDbContext db,
        int score,
        string suffix = "0",
        Guid? secondClientAgencyId = null,
        DuplicateCandidateStatus status = DuplicateCandidateStatus.ToReview)
    {
        var first = await TestClientFactory.SeedIndividualAsync(
            db, _tenantId, _agencyId, clientNumber: $"ABJ-2026-00{suffix}01");

        var secondClient = TestClientFactory.Individual(
            _tenantId, secondClientAgencyId ?? _agencyId, clientNumber: $"ABJ-2026-00{suffix}02");
        var second = await TestClientFactory.SeedAsync(db, secondClient);

        var (a, b) = DuplicatesTestDoubles.Canonical(first.Id, second.Id);
        var candidate = DuplicateCandidate.Detect(
            _tenantId, a, b, score, Reasons, $"fp-a-{suffix}", $"fp-b-{suffix}", DateTimeOffset.UtcNow);

        if (status == DuplicateCandidateStatus.Rejected)
            candidate.Reject(Guid.NewGuid(), DateTimeOffset.UtcNow);
        else if (status == DuplicateCandidateStatus.Merged)
            candidate.MarkMerged(Guid.NewGuid(), DateTimeOffset.UtcNow);

        db.DuplicateCandidates.Add(candidate);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (first, second);
    }
}
