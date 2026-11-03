namespace Sankore.Modules.Leads.Tests.Features.ListLeads;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// « Mes leads » (2026-11-03): an agent's leads are those assigned to them, or theirs as owner
/// while nobody is assigned — the list's « Agent » column. Filtering on the owner alone missed every
/// lead dispatched to an agent who is not its owner.
/// </summary>
public sealed class ListLeadsAgentFilterTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly TestDbContextFactory _db;

    public ListLeadsAgentFilterTests() => _db = new TestDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_agent_holds_the_leads_assigned_to_them_and_those_they_own_unassigned()
    {
        var assigned = Lead(owner: _other, assignee: _agent);
        var owned = Lead(owner: _agent, assignee: null);
        var ownedButHandedOver = Lead(owner: _agent, assignee: _other);
        var someoneElses = Lead(owner: _other, assignee: null);
        await using (var seed = _db.CreateContext())
        {
            seed.Leads.AddRange(assigned, owned, ownedButHandedOver, someoneElses);
            await seed.SaveChangesAsync();
        }

        await using var db = _db.CreateContext();
        var page = await new ListLeadsHandler(db).Handle(new ListLeadsQuery(AgentId: _agent), default);

        page.Value.Items.Select(l => l.Id).Should().BeEquivalentTo([assigned.Id, owned.Id]);
    }

    private Lead Lead(Guid owner, Guid? assignee)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.SetOwner(owner);
        if (assignee is { } agent)
            lead.AssignTo(LeadAssignment.Create(_tenantId, lead.Id, agent, DispatchingStrategy.RoundRobin, 1,
                DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow), null);
        return lead;
    }
}
