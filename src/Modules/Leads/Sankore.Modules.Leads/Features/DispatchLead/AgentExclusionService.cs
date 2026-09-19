namespace Sankore.Modules.Leads.Features.DispatchLead;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Manages temporary per-task agent exclusions (US-M13-084).
///
/// When an agent declines a task, they are added to a cached exclusion list
/// for that specific task. The exclusion TTL is aligned with the tenant's
/// <c>DispatchingRule.DeclineExclusionTtl</c>.
///
/// Storage: a single JSON array per task key — <c>agent-task-excl:{tenantId}:{taskId}</c>.
/// Uses <see cref="IDistributedCache"/> (Redis in prod, null in unit tests).
/// When cache is unavailable the exclusion is best-effort (not persisted).
/// </summary>
internal sealed class AgentExclusionService(IDistributedCache? cache = null)
{
    public static string CacheKey(Guid tenantId, Guid taskId)
        => $"agent-task-excl:{tenantId}:{taskId}";

    /// <summary>
    /// Adds <paramref name="agentId"/> to the exclusion list for the given task.
    /// The entire list shares the same TTL — reset on each new decline.
    /// </summary>
    public async Task ExcludeAsync(
        Guid tenantId, Guid taskId, Guid agentId, TimeSpan ttl, CancellationToken ct)
    {
        if (cache is null) return;

        var key     = CacheKey(tenantId, taskId);
        var current = await GetExcludedSetAsync(key, ct);
        current.Add(agentId);

        var opts = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        };

        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(current),
            opts,
            ct);
    }

    /// <summary>
    /// Returns the set of agent IDs currently excluded from dispatch for
    /// the given task. Returns an empty set when cache is unavailable or
    /// no exclusions exist.
    /// </summary>
    public async Task<HashSet<Guid>> GetExcludedAgentIdsAsync(
        Guid tenantId, Guid taskId, CancellationToken ct)
    {
        if (cache is null) return [];

        return await GetExcludedSetAsync(CacheKey(tenantId, taskId), ct);
    }

    private async Task<HashSet<Guid>> GetExcludedSetAsync(string key, CancellationToken ct)
    {
        var json = await cache!.GetStringAsync(key, ct);
        if (json is null) return [];

        var list = JsonSerializer.Deserialize<List<Guid>>(json);
        return list is null ? [] : new HashSet<Guid>(list);
    }
}
