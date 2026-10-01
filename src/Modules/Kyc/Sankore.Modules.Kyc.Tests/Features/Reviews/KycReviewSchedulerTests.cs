namespace Sankore.Modules.Kyc.Tests.Features.Reviews;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// KYC-B-07 — the periodicity of a review is a tenant parameter read from the vigilance level,
/// never a constant compiled into a handler.
/// </summary>
public sealed class KycReviewSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Today = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidatedOn = new(2026, 10, 1);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FixedClock _clock = new(Today);
    private readonly KycReviewScheduler _scheduler;

    public KycReviewSchedulerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _scheduler = new KycReviewScheduler(_db, new KycSettingsService(_db, _clock), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData(KycVigilanceLevel.High, 2027)]      // review-years-high = 1
    [InlineData(KycVigilanceLevel.Standard, 2029)]  // review-years-standard = 3
    [InlineData(KycVigilanceLevel.Low, 2031)]       // review-years-low = 5
    public async Task The_due_date_is_the_validation_date_plus_the_periodicity_of_the_level(
        KycVigilanceLevel level, int expectedYear)
    {
        // The three factory defaults of KycSettingKeys, read through the settings service so a
        // tenant with no row still gets the compiled-in periodicity.
        var due = await _scheduler.ComputeDueDateAsync(_tenantId, level, ValidatedOn, default);

        due.Should().Be(new DateOnly(expectedYear, 10, 1));
    }

    [Fact]
    public async Task The_tenant_value_wins_over_the_factory_default()
    {
        // The compliance officer shortened the high-risk periodicity from the settings screen;
        // no deployment, and the scheduler must honour it from the next validation on.
        await OverrideAsync(KycSettingKeys.ReviewYearsHigh, "2");

        var due = await _scheduler.ComputeDueDateAsync(
            _tenantId, KycVigilanceLevel.High, ValidatedOn, default);

        due.Should().Be(new DateOnly(2028, 10, 1));
    }

    [Fact]
    public async Task A_zero_periodicity_falls_back_to_one_year_rather_than_a_date_in_the_past()
    {
        // A misconfigured "0" would otherwise make every file due the day it is approved.
        await OverrideAsync(KycSettingKeys.ReviewYearsStandard, "0");

        var due = await _scheduler.ComputeDueDateAsync(
            _tenantId, KycVigilanceLevel.Standard, ValidatedOn, default);

        due.Should().Be(new DateOnly(2027, 10, 1));
    }

    [Fact]
    public async Task A_periodic_schedule_plants_a_row_and_caches_the_date_on_the_file()
    {
        var file = await SeedApprovedFileAsync(KycVigilanceLevel.Standard);

        var review = await _scheduler.SchedulePeriodicAsync(file, ValidatedOn, default);
        await _db.SaveChangesAsync();

        review.Trigger.Should().Be(KycReviewTrigger.Periodic);
        review.Status.Should().Be(KycReviewStatus.Scheduled);
        review.DueDate.Should().Be(new DateOnly(2029, 10, 1));

        // Every screen reads NextReviewDate from the file rather than querying the schedule table.
        var reloaded = await _db.KycFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        reloaded.NextReviewDate.Should().Be(new DateOnly(2029, 10, 1));
    }

    [Fact]
    public async Task An_event_review_only_moves_the_cached_date_earlier()
    {
        // A transaction alert today does not get to push a periodic deadline out by three years,
        // and it does not get to leave the file advertising a deadline three years away either.
        var file = await SeedApprovedFileAsync(KycVigilanceLevel.Standard);
        await _scheduler.SchedulePeriodicAsync(file, ValidatedOn, default);
        await _db.SaveChangesAsync();

        var raised = _scheduler.ScheduleEvent(file, ValidatedOn, "Alerte transaction inhabituelle.");
        await _db.SaveChangesAsync();

        raised.Trigger.Should().Be(KycReviewTrigger.Event);
        raised.Reason.Should().Be("Alerte transaction inhabituelle.");

        var reloaded = await _db.KycFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        reloaded.NextReviewDate.Should().Be(ValidatedOn);

        // Both rows survive: the periodic deadline is still owed once the alert is dealt with.
        (await _db.KycReviewSchedules.AsNoTracking().CountAsync(r => r.KycFileId == file.Id))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_later_event_review_leaves_an_earlier_periodic_deadline_alone()
    {
        var file = await SeedApprovedFileAsync(KycVigilanceLevel.High);   // due in 1 year
        await _scheduler.SchedulePeriodicAsync(file, ValidatedOn, default);
        await _db.SaveChangesAsync();

        _scheduler.ScheduleEvent(file, new DateOnly(2030, 1, 1), "Revue demandée par la direction.");
        await _db.SaveChangesAsync();

        var reloaded = await _db.KycFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        reloaded.NextReviewDate.Should().Be(new DateOnly(2027, 10, 1));
    }

    private async Task OverrideAsync(string key, string value)
    {
        var setting = KycSetting.FromDefault(_tenantId, KycSettingKeys.Find(key)!, _clock);
        setting.Update(value, Guid.NewGuid(), _clock);
        _db.KycSettings.Add(setting);
        await _db.SaveChangesAsync();
    }

    private async Task<KycFile> SeedApprovedFileAsync(KycVigilanceLevel level)
    {
        var agent = Guid.NewGuid();
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, agent, _clock, vigilanceLevel: level);

        file.SubmitForVerification(agent, _clock);
        file.RecordVerification(90, KycConfidenceLevel.High, _clock);
        file.Approve(KycTier.Full, Guid.NewGuid(), _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }
}

/// <summary>
/// A clock the test owns. The repo has no FakeTimeProvider package, and the review rules are all
/// about which day it is — "today" coming from the machine would make them unpinnable.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
