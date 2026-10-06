namespace Sankore.Modules.Leads.Features.DispatchLead;

/// <summary>
/// Why an agent scored what they scored, as data rather than as a JSON string.
///
/// The scorer used to hand this back already serialized, which suited the two audit columns that
/// store it (<c>LeadAssignment.CompatibilityFactorsJson</c>, <c>CrmTask.CompatibilityFactorsJson</c>,
/// both jsonb) but made every reader parse an untyped blob — including the dispatch-preview API,
/// whose consumers then had to re-declare the shape by hand and keep it in step. Typing it moves
/// that shape into the contract: the preview exposes it as an object, the OpenAPI document
/// describes it, and the generated clients get it for free.
///
/// The two columns still hold a string: they are an audit record of what was computed at the
/// time, and serializing at the persistence edge keeps old rows readable without a migration.
///
/// Only <c>CompatibilityScoringStrategy</c> fills this in. Round-robin and weighted round-robin
/// rank by load alone and legitimately produce no factors, so every reader must treat it as
/// optional rather than assume a breakdown exists.
/// </summary>
/// <param name="Weight">The rule's configured weight for the factor — its maximum contribution.</param>
/// <param name="Contribution">What the factor actually added to the total, after weighting.</param>
public sealed record CompatibilityFactors(
    LanguageFactor Language,
    ProductFactor Product,
    GeographyFactor Geography,
    WorkloadFactor Workload,
    PerformanceFactor Performance,
    AgencyFactor Agency);

/// <param name="Matched">Whether the agent speaks the lead's preferred language.</param>
public sealed record LanguageFactor(bool Matched, double Weight, double Contribution);

/// <param name="Matched">Whether the agent is specialised in the product the lead wants.</param>
public sealed record ProductFactor(bool Matched, double Weight, double Contribution);

/// <param name="DistanceKm">
/// Null when either side has no location — which is not the same as "far away", and is why this
/// is nullable rather than zero.
/// </param>
/// <param name="DecayFactor">The 0-1 multiplier distance applied to the weight.</param>
public sealed record GeographyFactor(
    double? DistanceKm, double DecayFactor, double Weight, double Contribution);

/// <param name="CombinedLoadRatio">
/// The mean of the lead and task ratios. The contribution is the weight times
/// <c>1 - CombinedLoadRatio</c>, so a busy agent scores LOWER here, not higher.
/// </param>
public sealed record WorkloadFactor(
    int ActiveLeads,
    int MaxLeads,
    double LeadLoadRatio,
    int OpenTasks,
    int MaxTasks,
    double TaskLoadRatio,
    double CombinedLoadRatio,
    double Weight,
    double Contribution);

public sealed record PerformanceFactor(
    double ConversionRate30d, double Weight, double Contribution);

/// <param name="Matched">Whether the agent belongs to the lead's preferred agency.</param>
public sealed record AgencyFactor(
    bool Matched,
    Guid AgentAgencyId,
    Guid? PreferredAgencyId,
    double Weight,
    double Contribution);
