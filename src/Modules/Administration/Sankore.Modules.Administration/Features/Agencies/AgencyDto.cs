using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Features.Agencies;

public sealed record AgencyDto(
    Guid Id,
    string Name,
    string Code,
    string Description,
    string AgencyType,
    Guid? ParentAgencyId,
    bool IsHeadQuarterAgency,
    bool IsActive,
    string? AddressStreet,
    string? AddressCity,
    string? AddressState,
    string? AddressCountry,
    string? AddressZipCode,
    double? Latitude,
    double? Longitude,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    /// <summary>Who runs the agency, or null when the post is vacant.</summary>
    Guid? ManagerUserId,
    /// <summary>Resolved for display so a list does not need one round-trip per agency.</summary>
    string? ManagerFullName);
