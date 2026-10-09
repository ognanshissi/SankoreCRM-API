namespace Sankore.Modules.Integration.Features.Mappings;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// One mapping row as the administration screen reads it.
///
/// <para>
/// <see cref="Domain"/> is a string and not the enum: the front-end branches on the eight names
/// and must not depend on the ordinals, which is the same reason the route segment is a name.
/// <see cref="Version"/> is PostgreSQL's <c>xmin</c>, echoed back on the next write.
/// </para>
/// </summary>
public sealed record MappingDto(
    Guid Id,
    Guid ConnectionId,
    string Domain,
    string CrmCode,
    string ExternalCode,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

internal static class MappingDtoMapper
{
    internal static MappingDto ToDto(this IntegrationMapping m) => new(
        m.Id,
        m.ConnectionId,
        m.Domain.ToString(),
        m.CrmCode,
        m.ExternalCode,
        m.Label,
        m.CreatedAt,
        m.UpdatedAt,
        m.Version);
}
