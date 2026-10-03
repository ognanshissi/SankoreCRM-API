namespace Sankore.Modules.Leads.Features.DispatchLead;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Computes and caches an agent's open CRM task count (Pending + InProgress)
/// on the critical dispatch path (US-M13-082).
///
/// Cache strategy: per-agent key, TTL 30 s. Invalidated explicitly by every
/// command that mutates a task's status or assignment so that a saturated agent
/// is never picked between a state change and the next TTL expiry.
///
/// The service uses IgnoreQueryFilters() because it may be called from contexts
/// without an ambient ITenantContext (e.g. MassTransit consumers). The tenantId
/// is always passed explicitly for tenant isolation.
/// </summary>
internal sealed class AgentCapacityService(
    LeadsDbContext db,
    IDistributedCache? cache = null)
{
    private static readonly DistributedCacheEntryOptions CacheOpts = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };

    public static string CacheKey(Guid tenantId, Guid agentId)
        => $"agent-task-cap:{tenantId}:{agentId}";

    /// <summary>
    /// Returns the number of open (Pending | InProgress) CRM tasks assigned
    /// to the agent. Result is served from Redis when available; falls back
    /// to a COUNT query and re-populates the cache.
    /// </summary>
    public async Task<int> GetOpenTaskCountAsync(
        Guid tenantId, Guid agentId, CancellationToken ct)
    {
        if (cache is not null)
        {
            var hit = await cache.GetStringAsync(CacheKey(tenantId, agentId), ct);
            if (hit is not null && int.TryParse(hit, out var cached))
                return cached;
        }

        var count = await db.CrmTasks
            .IgnoreQueryFilters()
            .CountAsync(t => t.TenantId       == tenantId
                          && t.AssignedAgentId == agentId
                          && (t.Status == CrmTaskStatus.Pending
                           || t.Status == CrmTaskStatus.InProgress), ct);

        if (cache is not null)
            await cache.SetStringAsync(CacheKey(tenantId, agentId), count.ToString(), CacheOpts, ct);

        return count;
    }

    /// <summary>
    /// Explicitly removes the cached count for an agent after a mutation that can change
    /// the count. Called by CompleteTask, CancelTask, AssignTask and DispatchTask.
    /// NOT by StartTask: the count includes both Pending and InProgress, so moving a task
    /// from one to the other leaves it unchanged and invalidating would be a wasted round
    /// trip. This comment used to list StartTask, which injected the service without ever
    /// calling it.
    /// </summary>
    public async Task InvalidateAsync(Guid tenantId, Guid agentId, CancellationToken ct)
    {
        if (cache is not null)
            await cache.RemoveAsync(CacheKey(tenantId, agentId), ct);
    }
}
