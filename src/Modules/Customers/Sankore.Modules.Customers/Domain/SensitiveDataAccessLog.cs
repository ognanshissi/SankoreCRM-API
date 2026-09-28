namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One audited reveal of a sensitive field. Written before the clear value is returned, so the
/// log is never missing an access, and counted per rolling hour to enforce
/// <see cref="CustomerSettingKeys.RevealLimitPerHour"/>.
/// </summary>
public sealed class SensitiveDataAccessLog
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid ActorUserId { get; private set; }

    /// <summary>Name of a <see cref="SensitiveField"/> value.</summary>
    public string FieldName { get; private set; } = default!;

    public DateTimeOffset AccessedAt { get; private set; }
    public string? CorrelationId { get; private set; }

    private SensitiveDataAccessLog() { } // EF Core

    public static SensitiveDataAccessLog Record(
        Guid tenantId,
        Guid clientId,
        Guid actorUserId,
        string fieldName,
        DateTimeOffset accessedAt,
        string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new DomainException("Field name is required.", "SensitiveDataAccessLog.FieldName.Required");

        return new SensitiveDataAccessLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            ActorUserId = actorUserId,
            FieldName = fieldName.Trim(),
            AccessedAt = accessedAt,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim(),
        };
    }
}
