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
    DateTimeOffset? UpdatedAt);
