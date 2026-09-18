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
    Guid? ConvertedToCustomerId);
