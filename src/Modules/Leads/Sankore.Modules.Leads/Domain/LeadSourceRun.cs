namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tracks a single execution of a lead source (pull cycle, webhook batch, import job).
/// Provides run-level metrics (fetched, ingested, rejected, duplicates).
/// </summary>
public sealed class LeadSourceRun : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid SourceId { get; private set; }

    public LeadSourceRunStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public int FetchedCount { get; private set; }
    public int IngestedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int DuplicateCount { get; private set; }

    public string? ErrorMessage { get; private set; }

    private LeadSourceRun() { }

    public static LeadSourceRun Start(Guid tenantId, Guid sourceId, TimeProvider clock)
        => new()
        {
            Id        = Guid.NewGuid(),
            TenantId  = tenantId,
            SourceId  = sourceId,
            Status    = LeadSourceRunStatus.Running,
            StartedAt = clock.GetUtcNow(),
        };

    public void Complete(int fetched, int ingested, int rejected, int duplicates, TimeProvider clock)
    {
        FetchedCount   = fetched;
        IngestedCount  = ingested;
        RejectedCount  = rejected;
        DuplicateCount = duplicates;
        Status         = LeadSourceRunStatus.Completed;
        CompletedAt    = clock.GetUtcNow();
    }

    public void Fail(string error, TimeProvider clock)
    {
        ErrorMessage = error;
        Status       = LeadSourceRunStatus.Failed;
        CompletedAt  = clock.GetUtcNow();
    }
}

public enum LeadSourceRunStatus
{
    Running,
    Completed,
    Failed
}
