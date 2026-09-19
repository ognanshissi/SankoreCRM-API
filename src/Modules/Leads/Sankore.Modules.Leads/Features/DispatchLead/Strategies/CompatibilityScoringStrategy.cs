namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Default and recommended strategy (F13.10 / US-M13-072 / US-M13-082): scores
/// every candidate via <see cref="CompatibilityScorer"/> and returns them ranked.
///
/// Two Redis caches are used on this critical path:
///   • Agent capacity cache (30s TTL, managed by <see cref="AgentCapacityService"/>):
///     the agent's open CRM task count. Invalidated explicitly after every task
///     status or assignment mutation.
///   • Compat-score cache (60s TTL): the full scored result. The cache key includes
///     the open task count, so a task status change that invalidates the capacity
///     cache also bypasses the old compat-score entry — the next dispatch for the
///     same agent/lead pair fetches a fresh capacity and produces a fresh key.
///
/// When no cache is registered (unit tests) both paths fall through to direct
/// computation with no correctness impact.
/// </summary>
internal sealed class CompatibilityScoringStrategy(
    IDistributedCache? cache = null,
    AgentCapacityService? capacityService = null) : IDispatchingStrategy
{
    private static readonly DistributedCacheEntryOptions CacheOpts = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    public async Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
    {
        var results = new List<ScoredCandidate>(candidates.Count);

        foreach (var agent in candidates)
        {
            // Fetch the open task count (served from Redis when available).
            // Including it in the compat-score cache key ensures stale scores
            // from before a task status change are never served.
            var openTaskCount = capacityService is not null
                ? await capacityService.GetOpenTaskCountAsync(lead.TenantId, agent.Id, ct)
                : 0;

            CompatibilityScoreResult scoreResult;

            if (cache is not null)
            {
                // Key encodes the task count so any change auto-busts the entry.
                var cacheKey = $"compat-score:{agent.Id}:{lead.Id}:{rules.Id}:t{openTaskCount}";
                var cached   = await cache.GetStringAsync(cacheKey, ct);

                if (cached is not null)
                {
                    var cached_obj = JsonSerializer.Deserialize<CachedScore>(cached)!;
                    scoreResult    = new CompatibilityScoreResult(cached_obj.Ts, cached_obj.Fj);
                }
                else
                {
                    scoreResult = scorer.Score(lead, agent, rules, openTaskCount);
                    var payload = JsonSerializer.Serialize(new CachedScore(scoreResult.TotalScore, scoreResult.FactorsJson));
                    await cache.SetStringAsync(cacheKey, payload, CacheOpts, ct);
                }
            }
            else
            {
                scoreResult = scorer.Score(lead, agent, rules, openTaskCount);
            }

            results.Add(new ScoredCandidate(agent, scoreResult.TotalScore, scoreResult.FactorsJson));
        }

        return results.OrderByDescending(c => c.CompatibilityScore).ToList();
    }

    // Thin DTO kept private to this file — avoids polluting the namespace.
    private sealed record CachedScore(double Ts, string Fj);
}
