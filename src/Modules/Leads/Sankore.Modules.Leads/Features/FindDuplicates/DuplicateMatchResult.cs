namespace Sankore.Modules.Leads.Features.FindDuplicates;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// A candidate lead identified as a potential duplicate with a multi-signal confidence score.
/// </summary>
public sealed record DuplicateMatchResult(
    Guid LeadId,
    string FullName,
    string PhoneNumber,
    string? Email,
    string? NationalId,
    string? CustomerReference,
    LeadStatus Status,
    LeadSource Source,
    DateTimeOffset CapturedAt,
    /// <summary>0-100 confidence that this candidate is the same person as the probe.</summary>
    double ConfidenceScore,
    /// <summary>"Très probable" | "Probable" | "Possible" | "Faible"</summary>
    string ConfidenceLabel,
    /// <summary>Human-readable summary (e.g. "87% — Téléphone identique + Nom très similaire (91%)").</summary>
    string ConfidenceSummary,
    IReadOnlyList<MatchReason> MatchReasons);
