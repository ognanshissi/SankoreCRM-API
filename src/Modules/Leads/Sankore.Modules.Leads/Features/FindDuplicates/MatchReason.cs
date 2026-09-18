namespace Sankore.Modules.Leads.Features.FindDuplicates;

/// <summary>A single signal contributing to a duplicate match confidence score.</summary>
public sealed record MatchReason(
    /// <summary>Signal key (e.g. "phone", "email", "nationalId", "name", "location").</summary>
    string Key,
    /// <summary>Human-readable description (French).</summary>
    string Description,
    /// <summary>Points contributed by this signal toward the total confidence score.</summary>
    double Contribution,
    /// <summary>True for exact matches (phone, email, nationalId, customerRef, dob); false for fuzzy (name, location).</summary>
    bool IsExact);
