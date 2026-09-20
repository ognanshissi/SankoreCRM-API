namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Immutable log of a single interaction with a lead (call, meeting, email, visit, note, etc.).
/// Created via <see cref="LogActivity.LogActivityHandler"/>; never mutated after creation.
/// </summary>
public sealed class LeadActivity
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    public ActivityType Type { get; private set; }

    /// <summary>Short summary of the activity (e.g. "Follow-up call", "Demo meeting").</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>Free-text notes recorded during / after the interaction.</summary>
    public string? Notes { get; private set; }

    /// <summary>Id of the agent/user who performed (or logged) this activity.</summary>
    public Guid PerformedBy { get; private set; }

    /// <summary>When the activity was scheduled (for calls/meetings; null for ad-hoc notes).</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>When the activity was actually performed / logged.</summary>
    public DateTimeOffset PerformedAt { get; private set; }

    /// <summary>Duration in minutes (relevant for calls and meetings).</summary>
    public int? DurationMinutes { get; private set; }

    /// <summary>Outcome of the interaction (optional — not all activity types have an outcome).</summary>
    public ActivityOutcome? Outcome { get; private set; }

    // ── Attachments (US-M13-100) ─────────────────────────────────────────
    // References to external object storage (S3/Azure Blob/GCS); never inline blobs.

    /// <summary>
    /// JSON array of attachment references, e.g.
    /// [{"key":"docs/abc.pdf","name":"Proposal.pdf","contentType":"application/pdf","sizeBytes":12345}]
    /// Stored as JSONB; empty array when no attachments.
    /// </summary>
    public string? AttachmentsJson { get; private set; }

    // ── CTI integration (US-M13-101) ─────────────────────────────────────

    /// <summary>
    /// Opaque reference to the external CTI system call record (e.g. call SID).
    /// Never contains audio data — only an identifier for the external system.
    /// </summary>
    public string? CtiCallReference { get; private set; }

    /// <summary>True when this activity was auto-logged by the system (CTI, workflow, etc.).</summary>
    public bool IsSystemGenerated { get; private set; }

    // ── Field visit (US-M13-102) ─────────────────────────────────────────

    /// <summary>GPS coordinates captured during a field visit. Sensitive PII — consent E03 required.</summary>
    public GeoPoint? VisitLocation { get; private set; }

    /// <summary>
    /// Reference to a photo in external encrypted storage (Vault envelope).
    /// Never a blob — only the object key / URL.
    /// </summary>
    public string? VisitPhotoReference { get; private set; }

    /// <summary>
    /// Explicit retention deadline for sensitive visit data (GPS + photo).
    /// After this date, a background job should purge the sensitive fields.
    /// </summary>
    public DateTimeOffset? RetentionExpiresAt { get; private set; }

    private LeadActivity() { } // EF Core

    public static LeadActivity Create(
        Guid tenantId,
        Guid leadId,
        ActivityType type,
        string subject,
        Guid performedBy,
        string? notes = null,
        DateTimeOffset? scheduledAt = null,
        int? durationMinutes = null,
        ActivityOutcome? outcome = null,
        string? attachmentsJson = null,
        string? ctiCallReference = null,
        bool isSystemGenerated = false,
        GeoPoint? visitLocation = null,
        string? visitPhotoReference = null,
        DateTimeOffset? retentionExpiresAt = null)
        => new()
        {
            Id                  = Guid.NewGuid(),
            TenantId            = tenantId,
            LeadId              = leadId,
            Type                = type,
            Subject             = subject.Trim(),
            Notes               = notes?.Trim(),
            PerformedBy         = performedBy,
            ScheduledAt         = scheduledAt,
            PerformedAt         = DateTimeOffset.UtcNow,
            DurationMinutes     = durationMinutes,
            Outcome             = outcome,
            AttachmentsJson     = attachmentsJson,
            CtiCallReference    = ctiCallReference,
            IsSystemGenerated   = isSystemGenerated,
            VisitLocation       = visitLocation,
            VisitPhotoReference = visitPhotoReference,
            RetentionExpiresAt  = retentionExpiresAt,
        };
}
