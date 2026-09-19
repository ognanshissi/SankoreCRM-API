namespace Sankore.Modules.Leads.Features.DispatchLead;

using System.Text.Json;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Scores compatibility between a lead and a candidate agent (0-100),
/// combining language, product specialty, geographic proximity, current
/// workload, agency match, and historical conversion performance (F13.10,
/// US-M13-072). Returns a <see cref="CompatibilityScoreResult"/> that
/// carries both the final score and a per-factor breakdown (FactorsJson)
/// for post-hoc dispatch audit.
///
/// Internal to the DispatchLead slice.
/// </summary>
internal sealed class CompatibilityScorer
{
    public CompatibilityScoreResult Score(Lead lead, AgentSummary agent, DispatchingRule rules)
    {
        // 1. Language match
        bool languageMatch    = agent.SpokenLanguages.Contains(lead.PreferredLanguage, StringComparer.OrdinalIgnoreCase);
        double languageContrib = languageMatch ? rules.Weights.Language : 0;

        // 2. Product specialty match
        bool productMatch    = agent.Specialties.Contains(lead.InterestedProduct, StringComparer.OrdinalIgnoreCase);
        double productContrib = productMatch ? rules.Weights.Product : 0;

        // 3. Geographic proximity (closer = higher, decays with distance)
        double? distanceKm = lead.Location is not null && agent.CurrentLocation is not null
            ? lead.Location.DistanceKmTo(agent.CurrentLocation)
            : null;
        double decayFactor   = distanceKm.HasValue ? DistanceDecay(distanceKm.Value) : 0;
        double geoContrib    = rules.Weights.Geography * decayFactor;

        // 4. Workload balance (less loaded agents score higher)
        double loadRatio     = rules.MaxLeadsPerAgent <= 0
            ? 0
            : agent.ActiveLeadsCount / (double)rules.MaxLeadsPerAgent;
        double workloadContrib = rules.Weights.Workload * (1 - Math.Min(loadRatio, 1));

        // 5. Historical conversion performance
        double perfContrib   = rules.Weights.Performance * agent.ConversionRate30d;

        // 6. Agency match (agent in the lead's preferred agency)
        bool agencyMatch     = lead.PreferredAgencyId.HasValue && agent.AgencyId == lead.PreferredAgencyId.Value;
        double agencyContrib = agencyMatch ? rules.Weights.Agency : 0;

        var total = Math.Round(
            Math.Clamp(languageContrib + productContrib + geoContrib + workloadContrib + perfContrib + agencyContrib, 0, 100),
            2);

        var factorsJson = JsonSerializer.Serialize(new
        {
            language    = new { matched = languageMatch, weight = rules.Weights.Language, contribution = languageContrib },
            product     = new { matched = productMatch, weight = rules.Weights.Product, contribution = productContrib },
            geography   = new { distanceKm = distanceKm.HasValue ? Math.Round(distanceKm.Value, 2) : (double?)null, decayFactor, weight = rules.Weights.Geography, contribution = geoContrib },
            workload    = new { activeLeads = agent.ActiveLeadsCount, maxLeads = rules.MaxLeadsPerAgent, loadRatio = Math.Round(loadRatio, 3), weight = rules.Weights.Workload, contribution = workloadContrib },
            performance = new { conversionRate30d = agent.ConversionRate30d, weight = rules.Weights.Performance, contribution = perfContrib },
            agency      = new { matched = agencyMatch, agentAgencyId = agent.AgencyId, preferredAgencyId = lead.PreferredAgencyId, weight = rules.Weights.Agency, contribution = agencyContrib }
        });

        return new CompatibilityScoreResult(total, factorsJson);
    }

    private static double DistanceDecay(double km) => km switch
    {
        <= 1  => 1.0,
        <= 5  => 0.8,
        <= 10 => 0.5,
        <= 20 => 0.2,
        _     => 0.0
    };
}

/// <summary>
/// Carries the final compatibility score plus the JSON breakdown of
/// per-factor contributions — used both for ranking (score) and for
/// persisting the audit trail on the winning assignment (factorsJson).
/// </summary>
internal sealed record CompatibilityScoreResult(double TotalScore, string FactorsJson);
