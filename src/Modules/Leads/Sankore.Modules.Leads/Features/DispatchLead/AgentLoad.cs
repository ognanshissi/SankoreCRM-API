using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

namespace Sankore.Modules.Leads.Features.DispatchLead;

/// <summary>
/// Each candidate's real load, counted from the leads this module owns: the open leads currently
/// assigned to them, and how many of those are hot.
///
/// <para>
/// <c>AgentSummary.ActiveLeadsCount</c> and <c>HotLeadsCount</c> come from counters on the user
/// (<c>AppUser.AdjustLeadCounters</c>) that nothing ever updated: every agent read 0 for both. The
/// workload criterion therefore scored everyone alike, the round-robin strategies — which order by
/// that count — always picked the same first agent, and the anti-monopoly threshold could never
/// trip. An import of ten leads went, one after another, to the single best-scoring agent.
/// </para>
///
/// <para>
/// Counted here rather than maintained as a counter: an assignment, a reassignment, a conversion, a
/// loss and a reopening all change the figure, and a counter misses any path that forgets to bump
/// it — which is exactly how this one stayed at zero. One grouped query per dispatch, over the
/// candidates only.
/// </para>
/// </summary>
internal static class AgentLoad
{
    public static async Task<IReadOnlyList<AgentSummary>> WithLiveLoadAsync(
        LeadsDbContext db, Guid tenantId, IReadOnlyList<AgentSummary> agents, CancellationToken ct)
    {
        if (agents.Count == 0) return agents;

        // A List, not an array: on .NET 10 an array's Contains binds to the span extension.
        var ids = agents.Select(a => a.Id).ToList();

        var loads = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && l.CurrentAssignedId != null
                     && ids.Contains(l.CurrentAssignedId.Value)
                     && l.Status != LeadStatus.Converted
                     && l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Disqualified
                     && l.Status != LeadStatus.Archived)
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new
            {
                AgentId = g.Key,
                Active = g.Count(),
                Hot = g.Count(l => l.IntentLevel == LeadIntentLevel.Hot),
            })
            .ToDictionaryAsync(x => x.AgentId, ct);

        return agents
            .Select(a => loads.TryGetValue(a.Id, out var load)
                ? a with { ActiveLeadsCount = load.Active, HotLeadsCount = load.Hot }
                : a with { ActiveLeadsCount = 0, HotLeadsCount = 0 })
            .ToList();
    }
}
