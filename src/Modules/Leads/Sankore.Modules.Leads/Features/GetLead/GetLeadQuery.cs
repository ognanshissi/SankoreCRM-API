namespace Sankore.Modules.Leads.Features.GetLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed record GetLeadQuery(Guid LeadId) : IRequest<Result<LeadDto>>;

public sealed record LeadDto(
    Guid Id,
    Guid TenantId,
    string FullName,
    string? FirstName,
    string? LastName,
    string PhoneNumber,
    string? Email,
    string Gender,
    DateOnly? DateOfBirth,
    string? CompanyName,
    string? CompanyEmail,
    string? CompanyPhone,
    string? Website,
    string Status,
    string PipelineStage,
    string Source,
    string? Channel,
    string? Campaign,
    string? ExternalReference,
    string? Comment,
    DateTimeOffset CapturedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    string InterestedProduct,
    Money? DesiredAmount,
    string PreferredLanguage,
    double QualificationCompleteness,
    int Score,
    string IntentLevel,
    double? Latitude,
    double? Longitude,
    Guid? PreferredAgencyId,
    Guid? OwnerId,
    Guid? AgencyId,
    Guid? CurrentAssignedId,
    Guid? CurrentAssignmentId,
    DateTimeOffset? LastActivityAt,
    string? LossReason,
    DateTimeOffset? ConvertedAt,
    Guid? ConvertedToCustomerId,
    string? NationalId,
    string? CustomerReference,
    string ProspectType,
    /// <param name="LeadSourceConfigId">
    /// The configured source the lead arrived through, null when there is none (typed into the
    /// UI, imported from a file, produced by a merge). Distinct from <c>Source</c>, which is a
    /// coarse enum and cannot name a specific configured form or webhook.
    /// </param>
    Guid? LeadSourceConfigId = null,
    /// <param name="LeadSourceCode">
    /// The source's <c>Code</c>, resolved for display. Null when the lead carries no source, and
    /// also when the id no longer resolves — the reference is opaque with no foreign key, so a
    /// source can be archived out from under it.
    /// </param>
    string? LeadSourceCode = null,
    /// <param name="LeadSourceLabel">The source's human-readable <c>Label</c>, same caveats.</param>
    string? LeadSourceLabel = null);
