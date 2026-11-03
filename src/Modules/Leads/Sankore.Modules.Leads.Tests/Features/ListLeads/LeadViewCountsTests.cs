namespace Sankore.Modules.Leads.Tests.Features.ListLeads;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// The tab counts of the lead list (2026-11-03): each must be what the list shows when the tab is
/// opened, under the filters the tabs share — the source here.
/// </summary>
public sealed class LeadViewCountsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _source = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public LeadViewCountsTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_axis_counts_what_its_view_would_list()
    {
        var hotAndMine = Lead(_source, owner: _me, LeadIntentLevel.Hot);
        var warm = Lead(_source, owner: _other, LeadIntentLevel.Warm);
        var queued = Lead(_source, owner: null, LeadIntentLevel.Cold);
        queued.MarkAwaitingDispatch(DateTimeOffset.UtcNow);
        var elsewhere = Lead(Guid.NewGuid(), owner: _me, LeadIntentLevel.Hot);
        await using (var seed = _db.CreateContext())
        {
            seed.Leads.AddRange(hotAndMine, warm, queued, elsewhere);
            await seed.SaveChangesAsync();
        }

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_me);
        await using var db = _db.CreateContext();
        var counts = (await new GetLeadViewCountsHandler(db, currentUser)
            .Handle(new GetLeadViewCountsQuery(LeadSourceConfigId: _source), default)).Value;

        counts.Total.Should().Be(3);
        counts.Mine.Should().Be(1);
        counts.AwaitingDispatch.Should().Be(1);
        counts.ByStatus.Should().Equal(new Dictionary<string, int> { ["New"] = 3 });
        counts.ByIntent.Should().BeEquivalentTo(new Dictionary<string, int> { ["Hot"] = 1, ["Warm"] = 1, ["Cold"] = 1 });

        var hot = await new ListLeadsHandler(db).Handle(
            new ListLeadsQuery(LeadSourceConfigId: _source, IntentLevel: LeadIntentLevel.Hot), default);
        hot.Value.TotalCount.Should().Be(counts.ByIntent["Hot"]);
    }

    private Lead Lead(Guid source, Guid? owner, LeadIntentLevel intent)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithSourceConfig(source).Fresh().Build();
        if (owner is { } o) lead.SetOwner(o);
        lead.UpdateIntentLevel(intent);
        return lead;
    }
}
