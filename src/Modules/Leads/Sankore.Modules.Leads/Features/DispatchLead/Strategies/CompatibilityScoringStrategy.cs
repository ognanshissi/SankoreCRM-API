namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Default and recommended strategy (F13.10 / US-M13-072): scores every
/// candidate via <see cref="CompatibilityScorer"/> and returns them ranked.
///
/// Results are cached in Redis (IDistributedCache) with a 60-second TTL
/// because agent load is volatile — several leads may be dispatched within
/// the same minute and caching avoids re-scoring the same agent pool
/// unnecessarily. When no cache is registered (e.g. unit tests) the scorer
/// is called directly with no degradation in correctness.
///
/// The handler applies the anti-monopoly filter afterward and picks the
/// top eligible candidate.
/// </summary>
internal sealed class CompatibilityScoringStrategy(IDistributedCache? cache = null) : IDispatchingStrategy
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
            CompatibilityScoreResult scoreResult;

            if (cache is not null)
            {
                var cacheKey = $"compat-score:{agent.Id}:{lead.Id}:{rules.Id}";
                var cached   = await cache.GetStringAsync(cacheKey, ct);

                if (cached is not null)
                {
                    var cached_obj = JsonSerializer.Deserialize<CachedScore>(cached)!;
                    scoreResult    = new CompatibilityScoreResult(cached_obj.Ts, cached_obj.Fj);
                }
                else
                {
                    scoreResult = scorer.Score(lead, agent, rules);
                    var payload = JsonSerializer.Serialize(new CachedScore(scoreResult.TotalScore, scoreResult.FactorsJson));
                    await cache.SetStringAsync(cacheKey, payload, CacheOpts, ct);
                }
            }
            else
            {
                scoreResult = scorer.Score(lead, agent, rules);
            }

            results.Add(new ScoredCandidate(agent, scoreResult.TotalScore, scoreResult.FactorsJson));
        }

        return results.OrderByDescending(c => c.CompatibilityScore).ToList();
    }

    // Thin DTO kept private to this file — avoids polluting the namespace.
    private sealed record CachedScore(double Ts, string Fj);
}
