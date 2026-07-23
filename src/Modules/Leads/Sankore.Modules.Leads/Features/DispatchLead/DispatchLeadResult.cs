namespace Sankore.Modules.Leads.Features.DispatchLead;

/// <summary>
/// Output DTO. Deliberately not the Lead or LeadAssignment entity itself:
/// the slice controls exactly what crosses the HTTP boundary, so internal
/// model changes never leak into the public API contract.
/// </summary>
public sealed record DispatchLeadResult(
    Guid AssignmentId,
    Guid AgentId,
    string AgentName,
    double CompatibilityScore,
    DateTimeOffset SlaDeadline);
