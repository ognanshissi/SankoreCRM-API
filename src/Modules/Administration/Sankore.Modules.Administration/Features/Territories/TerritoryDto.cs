namespace Sankore.Modules.Administration.Features.Territories;

public sealed record TerritoryDto(
    Guid Id,
    string Name,
    string Code,
    string Description,
    double? Latitude,
    double? Longitude,
    double RayonKm,
    List<string> ProductSpecialities,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
