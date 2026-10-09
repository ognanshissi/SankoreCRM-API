namespace Sankore.Modules.Integration.Features.Connections;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Everything the API ever says about a connection (INT-03).
///
/// <para>
/// <b>No record in this file, and nothing it reaches, may carry a credential.</b> The values live
/// in the M12 vault; a connection row keeps a vault REFERENCE and the API returns that reference,
/// never the secret behind it. <c>ConnectionDtoSecrecyTests</c> walks these records by reflection
/// and fails on a property whose very NAME evokes a secret — the cheapest guard against the way
/// this leak actually happens, which is someone adding one field to a response record.
/// </para>
/// </summary>
/// <param name="LastHealthStatus">
/// Null until a health check has run. False is a failed check, not "never ran" — the activation
/// screen needs to tell those apart, so the tri-state is carried through to the wire rather than
/// flattened to a bool.
/// </param>
internal sealed record ConnectionListDto(
    Guid Id,
    IntegrationFamily Family,
    IntegrationKind Kind,
    IntegrationMode Mode,
    string Name,
    bool IsActive,
    DateTimeOffset? LastHealthAt,
    bool? LastHealthStatus,
    DateTimeOffset UpdatedAt,
    uint Version)
{
    public static ConnectionListDto From(IntegrationConnection c) => new(
        c.Id, c.Family, c.Kind, c.Mode, c.Name, c.IsActive,
        c.LastHealthAt, c.LastHealthStatus, c.UpdatedAt, c.Version);
}

/// <summary>
/// The detail, settings included. The settings object is safe to return precisely because
/// <see cref="ConnectionSettings"/> forbids a credential-shaped field in the first place: it
/// holds coordinates plus a vault reference.
/// </summary>
internal sealed record ConnectionDetailDto(
    Guid Id,
    IntegrationFamily Family,
    IntegrationKind Kind,
    IntegrationMode Mode,
    string Name,
    ConnectionSettings? Settings,
    Guid? RelayAgentId,
    bool IsActive,
    bool HasPassedHealthCheck,
    DateTimeOffset? LastHealthAt,
    bool? LastHealthStatus,
    string? LastHealthDetail,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    uint Version)
{
    public static ConnectionDetailDto From(IntegrationConnection c) => new(
        c.Id, c.Family, c.Kind, c.Mode, c.Name, c.Settings, c.RelayAgentId, c.IsActive,
        c.HasPassedHealthCheck, c.LastHealthAt, c.LastHealthStatus, c.LastHealthDetail,
        c.CreatedAt, c.CreatedBy, c.UpdatedAt, c.UpdatedBy, c.Version);
}

/// <summary>
/// The answer of a health check, as the activation screen shows it.
/// </summary>
/// <param name="LatencyMs">
/// Carried because a CBS that answers in eight seconds is a different operational fact from one
/// that answers in eighty milliseconds, and only the operator can judge which is acceptable.
/// </param>
internal sealed record ConnectionHealthDto(
    bool IsHealthy,
    string? Detail,
    double? LatencyMs,
    DateTimeOffset CheckedAt)
{
    public static ConnectionHealthDto From(IntegrationHealth health) => new(
        health.IsHealthy, health.Detail, health.Latency?.TotalMilliseconds, health.CheckedAt);
}

/// <summary>
/// What a screen can show about one stored credential without ever receiving its value.
/// </summary>
/// <param name="MaskedValue">
/// The vault's own masked hint. Null when nothing is stored. Enough to tell two credentials apart
/// while rotating one, never enough to use either.
/// </param>
internal sealed record ConnectionSecretStatusDto(
    string Name,
    bool IsConfigured,
    string? MaskedValue,
    DateTimeOffset? ExpiresAt);
