namespace Sankore.Modules.Leads.Features.DispatchLead;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Users.PublicApi;

/// <summary>
/// Scores compatibility between a lead and a candidate agent (0-100),
/// combining language, product specialty, geographic proximity, current
/// workload, and historical conversion performance (F13.10).
///
/// Internal to the DispatchLead slice: no other feature may call this
/// directly. If another slice needs similar scoring in the future, it
/// gets its own copy or a deliberately promoted shared abstraction —
/// never a silent reach into this slice's internals.
/// </summary>
internal sealed class CompatibilityScorer
{
    public double Score(Lead lead, AgentSummary agent, DispatchingRule rules)
    {
        double score = 0;

        // 1. Language match
        if (agent.SpokenLanguages.Contains(lead.PreferredLanguage, StringComparer.OrdinalIgnoreCase))
            score += rules.Weights.Language;

        // 2. Product specialty match
        if (agent.Specialties.Contains(lead.InterestedProduct, StringComparer.OrdinalIgnoreCase))
            score += rules.Weights.Product;

        // 3. Geographic proximity (closer = higher, decays with distance)
        if (lead.Location is not null && agent.CurrentLocation is not null)
        {
            var distanceKm = lead.Location.DistanceKmTo(agent.CurrentLocation);
            score += rules.Weights.Geography * DistanceDecay(distanceKm);
        }

        // 4. Workload balance (less loaded agents score higher)
        var loadRatio = rules.MaxLeadsPerAgent <= 0
            ? 0
            : agent.ActiveLeadsCount / (double)rules.MaxLeadsPerAgent;
        score += rules.Weights.Workload * (1 - Math.Min(loadRatio, 1));

        // 5. Historical conversion performance
        score += rules.Weights.Performance * agent.ConversionRate30d;

        return Math.Round(Math.Clamp(score, 0, 100), 2);
    }

    private static double DistanceDecay(double km) => km switch
    {
        <= 1 => 1.0,
        <= 5 => 0.8,
        <= 10 => 0.5,
        <= 20 => 0.2,
        _ => 0.0
    };
}
