namespace Sankore.Modules.Kyc.Tests.Features.Reviews;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// KYC-B-07 — the daily sweep of one tenant: what comes due, what expires, what drops a tier.
///
/// The job's own <c>ExecuteAsync</c> only establishes the SYSTEM scope and resolves the services
/// below, exactly as every other Hangfire job of the repo does; the rules are in <c>RunAsync</c>
/// and that is what these tests drive.
/// </summary>
public sealed class ProcessTenantKycReviewsJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);

    /// <summary>Factory default of <c>review-grace-days</c>.</summary>
    private const int GraceDays = 30;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FixedClock _clock = new(Now);
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ProcessTenantKycReviewsJobTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private Task<KycReviewSweepReport> SweepAsync(Guid? tenantId = null)
        => ProcessTenantKycReviewsJob.RunAsync(
            _db,
            new KycSettingsService(_db, _clock),
            _publisher,
            _clock,
            NullLogger.Instance,
            tenantId ?? _tenantId,
            CancellationToken.None);

    // ── Coming due ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_review_that_is_not_due_yet_is_left_alone()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedReviewAsync(file, Today.AddDays(1));

        var report = await SweepAsync();

        report.MarkedDue.Should().Be(0);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Full);
        await _publisher.DidNotReceiveWithAnyArgs()
            .PublishAsync(Arg.Any<KycReviewDueEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_due_review_moves_the_file_UnderReview_and_announces_it_once()
    {
        var file = await SeedFileAsync(KycTier.Full);
        var review = await SeedReviewAsync(file, Today);

        var report = await SweepAsync();

        report.MarkedDue.Should().Be(1);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.UnderReview);
        (await ReloadReviewAsync(review.Id)).Status.Should().Be(KycReviewStatus.Due);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycReviewDueEvent>(e =>
                e.TenantId == _tenantId
                && e.KycFileId == file.Id
                && e.CustomerEntityId == file.CustomerId
                && e.DueDate == Today
                && e.Trigger == "Periodic"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweeping_twice_the_same_day_announces_nothing_a_second_time()
    {
        // At-least-once is the norm for a job: Hangfire retries, and an operator can re-queue it
        // from the dashboard. The review's own status is what makes the pass idempotent.
        var file = await SeedFileAsync(KycTier.Full);
        await SeedReviewAsync(file, Today);

        await SweepAsync();
        var second = await SweepAsync();

        second.MarkedDue.Should().Be(0);
        second.Expired.Should().Be(0);
        await _publisher.Received(1).PublishAsync(
            Arg.Any<KycReviewDueEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Grace period and expiry ─────────────────────────────────────────────

    [Fact]
    public async Task A_file_past_the_grace_period_expires_but_stays_open()
    {
        // Expiry is NOT a rejection: the customer keeps a valid, capped relationship and the file
        // is still theirs. IsOpen staying true is what the rest of the module reads.
        var file = await SeedFileAsync(KycTier.Full);
        var review = await SeedReviewAsync(file, Today.AddDays(-(GraceDays + 1)), markDue: true);
        await StartReviewAsync(file);

        var report = await SweepAsync();

        report.Expired.Should().Be(1);

        var reloaded = await ReloadAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.Expired);
        reloaded.IsOpen.Should().BeTrue();

        // The review stays Due: it is still owed, it is merely late.
        (await ReloadReviewAsync(review.Id)).Status.Should().Be(KycReviewStatus.Due);
    }

    [Fact]
    public async Task A_file_still_inside_the_grace_period_does_not_expire()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedReviewAsync(file, Today.AddDays(-(GraceDays - 1)), markDue: true);
        await StartReviewAsync(file);

        var report = await SweepAsync();

        report.Expired.Should().Be(0);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.UnderReview);
    }

    [Fact]
    public async Task The_grace_period_runs_from_the_due_date_not_from_the_day_the_job_runs()
    {
        // The job did not run for two months. The deadline was 60 days ago and the grace period
        // ran out 30 days ago, so the ONE sweep that finally happens must both raise the review
        // and expire the file. Starting the grace period from today would hand this tenant a free
        // extra month of non-compliance for having broken scheduling.
        var file = await SeedFileAsync(KycTier.Full);
        await SeedReviewAsync(file, Today.AddDays(-60));

        var report = await SweepAsync();

        report.MarkedDue.Should().Be(1);
        report.Expired.Should().Be(1);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Expired);
    }

    [Fact]
    public async Task A_review_due_inside_the_grace_window_is_raised_without_expiring_the_file()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedReviewAsync(file, Today.AddDays(-10));

        var report = await SweepAsync();

        report.MarkedDue.Should().Be(1);
        report.Expired.Should().Be(0);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.UnderReview);
    }

    // ── Expired identity document ───────────────────────────────────────────

    [Fact]
    public async Task A_full_file_whose_document_has_expired_is_downgraded_to_simplified()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedDocumentAsync(file, expiry: Today.AddDays(-1));

        var report = await SweepAsync();

        report.Downgraded.Should().Be(1);

        var reloaded = await ReloadAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.Simplified);
        reloaded.Tier.Should().Be(KycTier.Simplified);

        // Both tiers travel on the event: M03 (savings) and M07 (mobile money) need the previous
        // one to know a cached ceiling has to drop, not merely be refreshed.
        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycTierChangedEvent>(e =>
                e.TenantId == _tenantId
                && e.CustomerEntityId == file.CustomerId
                && e.PreviousTier == "Full"
                && e.CurrentTier == "Simplified"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_document_that_expires_today_is_still_valid()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedDocumentAsync(file, expiry: Today);

        var report = await SweepAsync();

        report.Downgraded.Should().Be(0);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Full);
    }

    [Fact]
    public async Task Downgrading_on_an_expired_document_does_not_repeat_on_the_next_sweep()
    {
        var file = await SeedFileAsync(KycTier.Full);
        await SeedDocumentAsync(file, expiry: Today.AddDays(-1));

        await SweepAsync();
        var second = await SweepAsync();

        second.Downgraded.Should().Be(0);
        await _publisher.Received(1).PublishAsync(
            Arg.Any<KycTierChangedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Isolation and resilience ────────────────────────────────────────────

    [Fact]
    public async Task A_file_of_another_tenant_is_never_touched()
    {
        // The sweep runs outside any HTTP request, so it ignores the query filters and carries its
        // own tenant predicate — which is exactly the line a cross-tenant leak would come from.
        var otherTenant = Guid.NewGuid();
        var mine = await SeedFileAsync(KycTier.Full);
        var theirs = await SeedFileAsync(KycTier.Full, tenantId: otherTenant);

        await SeedReviewAsync(mine, Today);
        await SeedReviewAsync(theirs, Today, tenantId: otherTenant);

        var report = await SweepAsync();

        report.MarkedDue.Should().Be(1);
        (await ReloadAsync(mine.Id)).Status.Should().Be(KycFileStatus.UnderReview);
        (await ReloadAsync(theirs.Id)).Status.Should().Be(KycFileStatus.Full);
    }

    [Fact]
    public async Task One_failing_file_does_not_stop_the_others()
    {
        // The outbox write of a single file fails — a deadlock, a transient disconnect. The other
        // 399 files of the tenant must still be swept: a sweep that abandons on the first failure
        // is a sweep that silently stops working the day one file is odd.
        var poison = await SeedFileAsync(KycTier.Full);
        var healthy = await SeedFileAsync(KycTier.Full);
        var poisonReview = await SeedReviewAsync(poison, Today);
        await SeedReviewAsync(healthy, Today);

        _publisher
            .PublishAsync(Arg.Any<KycReviewDueEvent>(), Arg.Any<CancellationToken>())
            .Returns(call => ((KycReviewDueEvent)call[0]!).KycFileId == poison.Id
                ? Task.FromException(new InvalidOperationException("outbox unavailable"))
                : Task.CompletedTask);

        var report = await SweepAsync();

        report.Failed.Should().Be(1);
        report.MarkedDue.Should().Be(1);

        (await ReloadAsync(healthy.Id)).Status.Should().Be(KycFileStatus.UnderReview);

        // Nothing of the failed file was committed: the status change and its outbox row go
        // together, so the next sweep retries it from a clean state.
        (await ReloadAsync(poison.Id)).Status.Should().Be(KycFileStatus.Full);
        (await ReloadReviewAsync(poisonReview.Id)).Status.Should().Be(KycReviewStatus.Scheduled);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    private async Task<KycFile> SeedFileAsync(KycTier tier, Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenantId;
        var agent = Guid.NewGuid();

        var file = KycFile.Open(tenant, Guid.NewGuid(), KycChannel.Agency, agent, _clock);
        file.SubmitForVerification(agent, _clock);
        file.RecordVerification(90, KycConfidenceLevel.High, _clock);
        file.Approve(tier, Guid.NewGuid(), _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private async Task StartReviewAsync(KycFile file)
    {
        file.StartReview(_clock);
        await _db.SaveChangesAsync();
    }

    private async Task<KycReviewSchedule> SeedReviewAsync(
        KycFile file, DateOnly dueDate, bool markDue = false, Guid? tenantId = null)
    {
        var review = KycReviewSchedule.Schedule(
            tenantId ?? _tenantId, file.Id, dueDate, KycReviewTrigger.Periodic, _clock);

        if (markDue) review.MarkDue(_clock);

        _db.KycReviewSchedules.Add(review);
        await _db.SaveChangesAsync();
        return review;
    }

    private async Task SeedDocumentAsync(KycFile file, DateOnly expiry)
    {
        var document = KycIdentityDocument.Create(
            file.TenantId, file.Id, "NationalIdCard",
            encryptedNumber: "v1:nonce:tag:ciphertext",
            numberBlindIndex: Guid.NewGuid().ToString("N"),
            clock: _clock,
            expiryDate: expiry);

        _db.KycIdentityDocuments.Add(document);
        await _db.SaveChangesAsync();
    }

    private async Task<KycFile> ReloadAsync(Guid id)
        => await _db.KycFiles.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == id);

    private async Task<KycReviewSchedule> ReloadReviewAsync(Guid id)
        => await _db.KycReviewSchedules.AsNoTracking().IgnoreQueryFilters()
            .SingleAsync(r => r.Id == id);
}
