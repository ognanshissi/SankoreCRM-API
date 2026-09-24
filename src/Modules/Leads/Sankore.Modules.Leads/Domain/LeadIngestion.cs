namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Records the ingestion of a single lead from an external source.
/// Links the lead to the source and optionally to the run that produced it.
/// Idempotent on (TenantId, SourceId, ExternalId).
/// </summary>
public sealed class LeadIngestion : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>FK to Lead. Guid.Empty when ingestion failed before lead creation.</summary>
    public Guid LeadId { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>FK to LeadSourceRun (nullable — manual/internal leads have no run).</summary>
    public Guid? RunId { get; private set; }

    /// <summary>Source-provided unique ID for idempotence. Unique per (TenantId, SourceId).</summary>
    public string? ExternalId { get; private set; }

    /// <summary>Raw payload received from the source (for audit/replay).</summary>
    public string? RawPayloadJson { get; private set; }

    public LeadIngestionStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset IngestedAt { get; private set; }

    private LeadIngestion() { }

    public static LeadIngestion Create(
        Guid tenantId,
        Guid leadId,
        Guid sourceId,
        TimeProvider clock,
        Guid? runId = null,
        string? rawPayloadJson = null,
        string? externalId = null)
        => new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            LeadId          = leadId,
            SourceId        = sourceId,
            RunId           = runId,
            ExternalId      = externalId,
            RawPayloadJson  = rawPayloadJson,
            Status          = LeadIngestionStatus.Accepted,
            IngestedAt      = clock.GetUtcNow(),
        };

    /// <summary>Creates a failed ingestion record (lead not created).</summary>
    public static LeadIngestion CreateFailed(
        Guid tenantId,
        Guid sourceId,
        string reason,
        TimeProvider clock,
        Guid? runId = null,
        string? rawPayloadJson = null,
        string? externalId = null)
        => new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            LeadId          = Guid.Empty,
            SourceId        = sourceId,
            RunId           = runId,
            ExternalId      = externalId,
            RawPayloadJson  = rawPayloadJson,
            Status          = LeadIngestionStatus.Failed,
            RejectionReason = reason,
            IngestedAt      = clock.GetUtcNow(),
        };

    public void Reject(string reason)
    {
        Status          = LeadIngestionStatus.Rejected;
        RejectionReason = reason;
    }

    public void MarkDuplicate()
    {
        Status = LeadIngestionStatus.Duplicate;
    }
}

public enum LeadIngestionStatus
{
    Accepted,
    Rejected,
    Duplicate,
    Failed
}
