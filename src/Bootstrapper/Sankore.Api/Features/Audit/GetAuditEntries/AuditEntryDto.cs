namespace Sankore.Api.Features.Audit.GetAuditEntries;

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid UserId,
    Guid TenantId,
    string Action,
    string Outcome,
    string? ErrorDetail,
    object? Payload,
    string? ResourceType,
    string? ResourceId,
    string? IpAddress,
    string? CorrelationId
);
