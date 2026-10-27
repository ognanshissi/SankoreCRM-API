namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.History;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>A project's history (owner, 2026-10-27): what changed, from what to what, who and when.</summary>
public sealed class ProjectHistoryTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T09:00:00Z");

    public ProjectHistoryTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _user.Id.Returns(_me);
        _user.TenantId.Returns(_tenantId);
        _user.IsAuthenticated.Returns(true);
        _user.Roles.Returns(["Administrator"]);
        _user.Permissions.Returns([]);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).ToDictionary(id => id, _ => "Awa Traoré"));
    }

    public void Dispose() => _factory.Dispose();

    private ProjectHistoryRecorder Recorder(LeadsDbContext db) => new(db, _user, new FixedTime(Now));

    private async Task<Guid> SeedAsync(decimal? amount = 90_000m)
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Obsèques Awa", "Obsèques Sérénité", _me,
            TimeProvider.System, estimatedAmount: amount is { } a ? new Money(a, "XOF") : null);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<List<ProjectHistoryEntry>> HistoryAsync(Guid projectId)
    {
        await using var db = _factory.CreateContext();
        return await db.ProjectHistory.Where(h => h.OpportunityId == projectId).OrderBy(h => h.Kind).ToListAsync();
    }

    private async Task UpdateAsync(UpdateOpportunityCommand cmd)
    {
        await using var db = _factory.CreateContext();
        var result = await new UpdateOpportunityHandler(db, new FixedTime(Now), history: Recorder(db)).Handle(cmd, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task An_update_writes_one_line_per_value_that_changed_with_its_author()
    {
        var id = await SeedAsync();

        await UpdateAsync(new UpdateOpportunityCommand(id, EstimatedAmount: 120_000m, EstimatedCurrency: "XOF",
            ExpectedCloseDate: DateTimeOffset.Parse("2026-12-01T12:00:00Z"), Product: "Obsèques Famille"));

        var lines = await HistoryAsync(id);
        lines.Select(l => (l.Kind, l.From, l.To)).Should().BeEquivalentTo(new[]
        {
            (ProjectHistoryKind.AmountChanged, (string?)"90000", (string?)"120000"),
            (ProjectHistoryKind.ProductChanged, "Obsèques Sérénité", "Obsèques Famille"),
            (ProjectHistoryKind.CloseDateChanged, null, "2026-12-01"),
        });
        lines.Should().OnlyContain(l => l.ActorId == _me && l.At == Now && !l.Reconstructed);
        lines.Single(l => l.Kind == ProjectHistoryKind.AmountChanged).Detail.Should().Be("XOF");
    }

    [Fact]
    public async Task An_update_that_changes_nothing_shown_writes_nothing()
    {
        var id = await SeedAsync();

        // The title, the need and the next action are not in the history; the same amount is no change.
        await UpdateAsync(new UpdateOpportunityCommand(id, Title: "Obsèques — famille Koné", Description: "Trois personnes",
            EstimatedAmount: 90_000m, EstimatedCurrency: "XOF", NextStep: "Rappeler"));

        (await HistoryAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_lost_project_keeps_its_reason()
    {
        var id = await SeedAsync();
        await using (var db = _factory.CreateContext())
        {
            var result = await new CloseOpportunityHandler(
                    db, Substitute.For<ICustomersModule>(), _admin, _user, TimeProvider.System,
                    NullLogger<CloseOpportunityHandler>.Instance, history: Recorder(db))
                .Handle(new CloseOpportunityCommand(id, OpportunityStage.ClosedLost, "Financement obtenu ailleurs"), CancellationToken.None);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        var line = (await HistoryAsync(id)).Single();
        line.Kind.Should().Be(ProjectHistoryKind.Lost);
        line.Detail.Should().Be("Financement obtenu ailleurs");
    }

    [Fact]
    public async Task A_step_move_names_both_steps_as_they_read()
    {
        var id = await SeedAsync();
        await using var db = _factory.CreateContext();
        var resolver = new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenantId));
        var steps = (await resolver.ResolveAsync("Obsèques Sérénité", CancellationToken.None)).Steps;
        var target = steps[2];

        var moved = await new MoveOpportunityToStepHandler(db, resolver, new ProjectStepTransition(new FixedTime(Now), history: Recorder(db)))
            .Handle(new MoveOpportunityToStepCommand(id, target.Id), CancellationToken.None);

        moved.IsSuccess.Should().BeTrue(moved.Error);
        var line = (await HistoryAsync(id)).Single();
        line.Kind.Should().Be(ProjectHistoryKind.StepChanged);
        line.From.Should().Be(steps[0].Label);
        line.To.Should().Be(target.Label);
    }

    [Fact]
    public async Task A_contract_is_recorded_once_however_often_it_is_asked_for()
    {
        var id = await SeedAsync();
        var holding = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            var project = await db.Opportunities.SingleAsync(o => o.Id == id);
            await Recorder(db).HoldingOpenedAsync(project, holding, CancellationToken.None);
            await Recorder(db).HoldingOpenedAsync(project, holding, CancellationToken.None);
        }

        var line = (await HistoryAsync(id)).Single();
        line.Kind.Should().Be(ProjectHistoryKind.HoldingOpened);
        line.RelatedId.Should().Be(holding);
    }

    [Fact]
    public async Task The_history_reads_most_recent_first_with_the_author_s_name()
    {
        var id = await SeedAsync();
        await using (var db = _factory.CreateContext())
        {
            var project = await db.Opportunities.SingleAsync(o => o.Id == id);
            db.ProjectHistory.AddRange(
                ProjectHistoryEntry.Record(project, ProjectHistoryKind.Created, null, Now.AddDays(-2), detail: "Obsèques Sérénité"),
                ProjectHistoryEntry.Record(project, ProjectHistoryKind.StepChanged, _me, Now, "Recueil du besoin", "Devis"));
            await db.SaveChangesAsync();
        }

        await using var read = _factory.CreateContext();
        var history = (await new GetProjectHistoryHandler(read, _admin, new FixedTenantContext(_tenantId), new LeadVisibility(_user))
            .Handle(new GetProjectHistoryQuery(id), CancellationToken.None)).Value!;

        history.Select(h => h.Kind).Should().Equal(ProjectHistoryKind.StepChanged, ProjectHistoryKind.Created);
        history[0].ActorName.Should().Be("Awa Traoré");
        history[1].ActorName.Should().BeNull("a line written by nobody names nobody");
    }

    [Fact]
    public async Task A_project_out_of_the_caller_s_perimeter_has_no_history_to_show()
    {
        var id = await SeedAsync();
        var agent = Substitute.For<ICurrentUser>();
        agent.Id.Returns(Guid.NewGuid());
        agent.IsAuthenticated.Returns(true);
        agent.Roles.Returns(["Agent"]);
        agent.Permissions.Returns([]);

        await using var db = _factory.CreateContext();
        var result = await new GetProjectHistoryHandler(db, _admin, new FixedTenantContext(_tenantId), new LeadVisibility(agent))
            .Handle(new GetProjectHistoryQuery(id), CancellationToken.None);

        result.Error.Should().Be(OpportunityErrors.NotFound);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
