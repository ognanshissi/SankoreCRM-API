namespace Sankore.Modules.Leads.Domain;

public sealed class LeadImportJob : Sankore.Shared.Kernel.AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid InitiatedBy { get; private set; }
    public string FileReference { get; private set; } = null!;
    public string OriginalFileName { get; private set; } = null!;
    public LeadImportStatus Status { get; private set; }
    public int TotalRows { get; private set; }
    public int Succeeded { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string? FailureDetailsJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private LeadImportJob() { }

    public static LeadImportJob Create(
        Guid tenantId, Guid initiatedBy,
        string fileReference, string originalFileName,
        TimeProvider clock)
    {
        return new LeadImportJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InitiatedBy = initiatedBy,
            FileReference = fileReference,
            OriginalFileName = originalFileName,
            Status = LeadImportStatus.Pending,
            CreatedAt = clock.GetUtcNow()
        };
    }

    public void MarkProcessing() => Status = LeadImportStatus.Processing;

    public void Complete(
        int totalRows, int succeeded, int skipped, int failed,
        string? failureDetailsJson, TimeProvider clock)
    {
        TotalRows = totalRows;
        Succeeded = succeeded;
        Skipped = skipped;
        Failed = failed;
        FailureDetailsJson = failureDetailsJson;
        Status = LeadImportStatus.Completed;
        CompletedAt = clock.GetUtcNow();
    }

    public void Fail(string error, TimeProvider clock)
    {
        Status = LeadImportStatus.Failed;
        ErrorMessage = error;
        CompletedAt = clock.GetUtcNow();
    }
}

public enum LeadImportStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
