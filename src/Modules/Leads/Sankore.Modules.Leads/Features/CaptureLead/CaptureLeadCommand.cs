namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
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
    /// <summary>
    /// When true, bypasses the duplicate-confirmation gate and forces creation
    /// even if a matching active lead already exists.
    /// </summary>
    bool Force = false
) : IRequest<Result<CaptureLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null;
}

/// <param name="LeadId">Id of the created lead. Null when <see cref="DuplicateDetected"/> is true.</param>
/// <param name="DuplicateDetected">True when potential duplicates were found and Force was not set.
/// The caller should surface <see cref="PotentialDuplicates"/> to the user and re-submit with Force=true.</param>
public sealed record CaptureLeadResult(
    Guid? LeadId,
    string? Status,
    bool DuplicateDetected = false,
    IReadOnlyList<PotentialDuplicateMatch>? PotentialDuplicates = null);

public sealed record PotentialDuplicateMatch(
    Guid LeadId,
    string FullName,
    string PhoneNumber,
    string? Email,
    string Status,
    IReadOnlyList<string> MatchedOn);
