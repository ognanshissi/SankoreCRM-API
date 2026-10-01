namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Who looked at a KYC image, which one, and when.
///
/// A KYC image is more sensitive than the encrypted number beside it: the picture carries the
/// number, the face and the address all at once. Encrypting it at rest answers "can a database
/// dump read it"; this answers the question a regulator actually asks after an identity theft —
/// "who in your branch opened this customer's papers, and when".
///
/// Deliberately a flat log and not an aggregate: it is append-only evidence, never corrected.
/// Mirrors M01's <c>SensitiveDataAccessLog</c>, which does the same for a revealed field.
/// </summary>
public sealed class KycDocumentAccessLog : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid KycFileId { get; private set; }

    /// <summary>The object that was opened. Opaque, and it carries no tenant or customer id.</summary>
    public string StorageRef { get; private set; } = default!;

    public Guid ActorUserId { get; private set; }
    public DateTimeOffset AccessedAt { get; private set; }

    /// <summary>Ties the read to the request that caused it, for an end-to-end trace.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>
    /// Why the image was opened, when the caller stated a reason. Free text an operator typed, so
    /// never logged and never shown outside the compliance screen.
    /// </summary>
    public string? Reason { get; private set; }

    private KycDocumentAccessLog() { }

    public static KycDocumentAccessLog Record(
        Guid tenantId, Guid kycFileId, string storageRef, Guid actorUserId,
        TimeProvider clock, string? correlationId = null, string? reason = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            StorageRef = storageRef,
            ActorUserId = actorUserId,
            AccessedAt = clock.GetUtcNow(),
            CorrelationId = correlationId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };
}
