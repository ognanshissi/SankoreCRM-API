namespace Sankore.Modules.Leads.Tests.TestSupport;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Gives an agent real open leads. The dispatch counts an agent's load from the leads assigned to
/// them (<c>AgentLoad</c>), not from <c>AgentSummary.ActiveLeadsCount</c>/<c>HotLeadsCount</c> —
/// so a test that wants a loaded or monopolising agent has to assign them leads.
/// </summary>
internal static class AgentLoadFixture
{
    public static async Task AssignOpenLeadsAsync(
        LeadsDbContext db, Guid tenantId, Guid agentId, int count, bool hot = false)
    {
        for (var i = 0; i < count; i++)
        {
            var lead = LeadTestBuilder.Create().WithTenant(tenantId).Build();
            if (hot) lead.UpdateIntentLevel(LeadIntentLevel.Hot);

            var assignment = LeadAssignment.Create(
                tenantId, lead.Id, agentId, DispatchingStrategy.RoundRobin, 50,
                slaDeadline: DateTimeOffset.UtcNow.AddHours(4), createdAt: DateTimeOffset.UtcNow);
            lead.AssignTo(assignment, currentAssignment: null);
            lead.ClearDomainEvents();

            db.Leads.Add(lead);
            db.LeadAssignments.Add(assignment);
        }
        await db.SaveChangesAsync();
    }
}
