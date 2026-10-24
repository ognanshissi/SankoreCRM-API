namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// « Crédit de campagne agricole : la date de clôture est critique » (propriétaire, 2026-10-24) —
/// un crédit accordé après le début des pluies ne sert plus. Le gabarit rend la date obligatoire,
/// et une date dépassée crée une tâche urgente.
/// </summary>
public sealed class CriticalCloseDateTests : IDisposable
{
    private const string Campaign = "Crédit de campagne";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T06:00:00Z");

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public CriticalCloseDateTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        var line = Guid.NewGuid();
        _admin.ResolveProductLineAsync(_tenant, Campaign, Arg.Any<CancellationToken>()).Returns(line);
        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>())
            .Returns(new List<ProductSummary> { new("CAMPAGNE", Campaign, "Loan", true, line) });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void A_critical_date_needs_a_template_that_tracks_the_forecast()
    {
        var created = ProjectTemplate.Create(_tenant, "Campagne", null, "CAMPAGNE", Steps(), Now,
            new ProjectForecastOptions(TracksForecast: false, CriticalCloseDate: true));

        created.Error.Should().Be(ProjectTemplateErrors.CriticalDateWithoutForecast);
    }

    [Fact]
    public async Task A_project_without_its_critical_date_is_refused()
    {
        await SeedTemplateAsync();
        var lead = await SeedLeadAsync();

        await using var db = _factory.CreateContext();
        var handler = new CreateOpportunityHandler(db, TimeProvider.System, _admin, Resolver(db));

        var without = await handler.Handle(new CreateOpportunityCommand(_tenant, lead, "Campagne 2027", Campaign), default);
        var with = await handler.Handle(
            new CreateOpportunityCommand(_tenant, lead, "Campagne 2027", Campaign, ExpectedCloseDate: Now.AddMonths(2)), default);

        without.Error.Should().Be(ProjectTemplateErrors.CloseDateRequired);
        with.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_passed_critical_date_raises_one_urgent_task_for_the_project_s_agent()
    {
        await SeedTemplateAsync();
        var lead = await SeedLeadAsync();
        await SeedProjectAsync(lead, Now.AddDays(-2));

        (await RunJobAsync(Now)).Should().Be(1);
        (await RunJobAsync(Now.AddDays(1))).Should().Be(0, "the same passed date is chased once");

        await using var db = _factory.CreateContext();
        var task = await db.CrmTasks.SingleAsync();
        task.Priority.Should().Be(CrmTaskPriority.Critical);
        task.AssignedAgentId.Should().Be(_agent, "the lead's agent follows a project with no owner");
        task.LeadId.Should().Be(lead);
    }

    [Fact]
    public async Task A_project_with_no_deadline_is_asked_for_one_once()
    {
        // Created before the template made the date critical: it can never be « late ».
        await SeedTemplateAsync();
        await SeedProjectAsync(await SeedLeadAsync(), closeOn: null);

        (await RunJobAsync(Now)).Should().Be(1);
        (await RunJobAsync(Now.AddDays(1))).Should().Be(0);

        await using var db = _factory.CreateContext();
        (await db.CrmTasks.SingleAsync()).Title.Should().StartWith("Date limite à fixer");
    }

    [Fact]
    public async Task An_estimated_date_is_not_chased()
    {
        await SeedTemplateAsync(critical: false);
        await SeedProjectAsync(await SeedLeadAsync(), Now.AddDays(-2));

        (await RunJobAsync(Now)).Should().Be(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<ProjectStepInput> Steps() =>
        [new(null, "Montage", OpportunityStage.NeedsAnalysis, 40), new(null, "Comité", OpportunityStage.Proposal, 70)];

    private ProjectTemplateResolver Resolver(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
        => new(db, _admin, new FixedTenantContext(_tenant));

    private async Task SeedTemplateAsync(bool critical = true)
    {
        await using var db = _factory.CreateContext();
        db.ProjectTemplates.Add(ProjectTemplate.Create(_tenant, "Campagne", null, "CAMPAGNE", Steps(), Now,
            new ProjectForecastOptions(AmountKind: ProjectAmountKind.Loan, CriticalCloseDate: critical)).Value!);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedLeadAsync()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithAgency(Guid.NewGuid()).WithProduct(Campaign).Build();
        typeof(Lead).GetProperty(nameof(Lead.CurrentAssignedId))!.SetValue(lead, (Guid?)_agent);
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private async Task SeedProjectAsync(Guid leadId, DateTimeOffset? closeOn)
    {
        await using var db = _factory.CreateContext();
        db.Opportunities.Add(Opportunity.CreateFromLead(_tenant, leadId, "Campagne 2027", Campaign, null, TimeProvider.System,
            expectedCloseDate: closeOn));
        await db.SaveChangesAsync();
    }

    private async Task<int> RunJobAsync(DateTimeOffset now)
    {
        await using var db = _factory.CreateContext();
        return await ProjectCloseDateOverdueJob.RunAsync(db, Resolver(db), new Clock(now), default);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
