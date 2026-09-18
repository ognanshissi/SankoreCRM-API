namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

public sealed record CaptureLeadCommand(
    Guid TenantId,
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender Gender = LeadGender.Unknown,
    DateOnly? DateOfBirth = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? Campaign = null,
    LeadChannel? Channel = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    Guid? AgentCollectedLeadId = null,
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null,
    LeadType ProspectType = LeadType.Individual,
    string? NationalId = null,
    string? CustomerReference = null,
    /// <summary>
    /// Controls whether a duplicate match blocks creation (409) or just warns (201 + DuplicateDetected=true).
    /// </summary>
    DuplicateGateMode GateMode = DuplicateGateMode.Block,
    /// <summary>
    /// Minimum confidence score (0-100) for a candidate to be considered a potential duplicate.
    /// Defaults to <see cref="IdentityMatchScorer.MinConfidence"/> (30).
    /// </summary>
    double MinConfidenceThreshold = IdentityMatchScorer.MinConfidence,
    /// <summary>
    /// When true, bypasses the duplicate gate entirely and forces creation regardless of matches.
    /// </summary>
    bool Force = false
) : IRequest<Result<CaptureLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null;
}

/// <param name="LeadId">Id of the created lead. Null only when <see cref="DuplicateDetected"/> is true AND <see cref="DuplicateGateMode.Block"/> was used.</param>
/// <param name="DuplicateDetected">
/// True when potential duplicates were found.
/// In <see cref="DuplicateGateMode.Block"/> mode the lead was NOT created (LeadId is null) — re-submit with Force=true to override.
/// In <see cref="DuplicateGateMode.Warn"/> mode the lead WAS created (LeadId is set) — surface the warning to the user.
/// </param>
public sealed record CaptureLeadResult(
    Guid? LeadId,
    string? Status,
    bool DuplicateDetected = false,
    IReadOnlyList<DuplicateMatchResult>? PotentialDuplicates = null);
