namespace Sankore.Modules.Leads.Tests.Features.Settings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.Features.Settings;
using Sankore.Modules.Leads.Features.Settings.PreviewRetention;
using Sankore.Modules.Leads.Tests.Features.Retention;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The only safety check that exists before an irreversible operation: how many leads tonight's
/// anonymisation run would destroy.
///
/// <para>
/// The case that matters most is <see cref="The_preview_and_the_job_select_the_same_leads"/>. A
/// preview is read as an approval of a number; if it selected different rows from the job, the
/// figure approved would not be the figure destroyed — and nothing would reveal the discrepancy,
/// because the leads are gone.
/// </para>
/// </summary>
public sealed class PreviewRetentionHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public PreviewRetentionHandlerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedClosedAsync(int monthsAgo, LeadStatus? forceStatus = null)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Awa Ouattara", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: Guid.NewGuid(), clock: TimeProvider.System,
            firstName: "Awa", lastName: "Ouattara", ownerId: Guid.NewGuid());

        if (forceStatus is null)
            lead.Close(LeadCloseReason.Lost, "Pas intéressé");

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        await using var backdate = _factory.CreateContext();
        var stored = await backdate.Leads.AsTracking().FirstAsync(l => l.Id == lead.Id);

        if (forceStatus is not null)
        {
            typeof(Lead).GetProperty(nameof(Lead.Status))!.SetValue(stored, forceStatus.Value);
        }

        // UpdatedAt is what the selection measures the retention period against, and only a
        // closure moves it in production.
        typeof(Lead).GetProperty(nameof(Lead.UpdatedAt))!
            .SetValue(stored, Now.AddMonths(-monthsAgo));

        await backdate.SaveChangesAsync();
        return lead;
    }

    private async Task<Sankore.Shared.Kernel.Result<RetentionPreviewDto>> PreviewAsync(
        int retentionMonths = 24)
    {
        await using var db = _factory.CreateContext();

        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenant);
        user.Id.Returns(_actor);

        var handler = new PreviewRetentionHandler(
            db,
            new FixedLeadSettings(retentionMonths),
            user,
            new FixedRetentionClock(Now));

        return await handler.Handle(new PreviewRetentionQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task A_lead_closed_longer_ago_than_the_period_is_due()
    {
        await SeedClosedAsync(monthsAgo: 30);

        var result = await PreviewAsync(retentionMonths: 24);

        result.IsSuccess.Should().BeTrue();
        result.Value!.DueNow.Should().Be(1);
        result.Value.RetentionMonths.Should().Be(24);
        result.Value.Cutoff.Should().Be(Now.AddMonths(-24));
        result.Value.OldestClosedAt.Should().Be(Now.AddMonths(-30));
    }

    [Fact]
    public async Task A_lead_closed_within_the_period_is_not_due()
    {
        await SeedClosedAsync(monthsAgo: 12);

        var result = await PreviewAsync(retentionMonths: 24);

        result.Value!.DueNow.Should().Be(0);
        result.Value.OldestClosedAt.Should().BeNull(
            "there is no oldest due lead when nothing is due");
    }

    [Fact]
    public async Task A_live_lead_is_never_due_however_old_it_is()
    {
        await SeedClosedAsync(monthsAgo: 120, forceStatus: LeadStatus.Open);

        var result = await PreviewAsync();

        result.Value!.DueNow.Should().Be(0,
            "a prospect nobody has closed is still a prospect");
    }

    [Fact]
    public async Task Lowering_the_period_makes_more_leads_due()
    {
        // The reason the endpoint exists: an administrator about to lower the period can see what
        // that costs BEFORE the night runs, rather than after.
        await SeedClosedAsync(monthsAgo: 18);

        (await PreviewAsync(retentionMonths: 24)).Value!.DueNow.Should().Be(0);
        (await PreviewAsync(retentionMonths: 12)).Value!.DueNow.Should().Be(1);
    }

    [Fact]
    public async Task A_period_of_zero_is_refused_rather_than_answered_with_zero()
    {
        // The job treats zero as "not configured" and SKIPS. A count of zero would read as
        // "nothing to destroy", which is the opposite of what a settings typo means.
        await SeedClosedAsync(monthsAgo: 30);

        var result = await PreviewAsync(retentionMonths: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadSettingErrors.RetentionNotConfigured);
    }

    [Fact]
    public async Task The_response_carries_the_per_run_ceiling_and_the_irreversibility_warning()
    {
        await SeedClosedAsync(monthsAgo: 30);

        var preview = (await PreviewAsync()).Value!;

        preview.MaxPerRun.Should().Be(AnonymizeClosedLeadsJob.MaxLeadsPerRun);
        preview.IrreversibleWarning.Should().Be(PreviewRetentionHandler.Warning);
    }

    [Fact]
    public async Task The_preview_and_the_job_select_the_same_leads()
    {
        // Both go through RetentionSelection.Due. This case is what stops the two drifting: it
        // seeds leads on both sides of the cutoff and in a live status, asks the preview, then
        // asserts the job anonymised exactly as many as the preview announced.
        await SeedClosedAsync(monthsAgo: 30);
        await SeedClosedAsync(monthsAgo: 26);
        await SeedClosedAsync(monthsAgo: 12);
        await SeedClosedAsync(monthsAgo: 60, forceStatus: LeadStatus.Qualifying);

        var announced = (await PreviewAsync(retentionMonths: 24)).Value!.DueNow;
        announced.Should().Be(2);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedRetentionClock(Now));
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton<ILeadSettings>(new FixedLeadSettings(24));
        await using var provider = services.BuildServiceProvider();

        var job = new AnonymizeClosedLeadsJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AnonymizeClosedLeadsJob>.Instance);

        await job.RunAsync(_tenant, CancellationToken.None);

        await using var db = _factory.CreateContext();
        var anonymised = await db.Leads.IgnoreQueryFilters()
            .CountAsync(l => l.TenantId == _tenant && l.IsAnonymized);

        anonymised.Should().Be(announced);

        // And the preview now reports nothing left, because an anonymised lead no longer matches
        // the selection — the property that makes the run self-advancing.
        (await PreviewAsync(retentionMonths: 24)).Value!.DueNow.Should().Be(0);
    }
}
