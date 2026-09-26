namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tracks one lead import run, whatever its source (uploaded file, Google Sheet,
/// Google Contacts). Mirrors <c>UserImportJob</c> in the Administration module.
/// </summary>
public sealed class LeadImportJob : Sankore.Shared.Kernel.AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid InitiatedBy { get; private set; }

    /// <summary>Where the rows come from — selects the reader at processing time.</summary>
    public LeadImportSourceType SourceType { get; private set; }

    /// <summary>
    /// Opaque reference: file key for File, spreadsheet URL for GoogleSheet,
    /// or the "google-contacts" marker for GoogleContacts.
    /// </summary>
    public string SourceReference { get; private set; } = null!;

    /// <summary>Original upload name. Null for the Google sources, which have no file.</summary>
    public string? OriginalFileName { get; private set; }

    /// <summary>
    /// Serialized <c>ImportDefaults</c> — values applied to rows that leave a field blank.
    /// Google Contacts carries no product or language, so the caller supplies them here.
    /// </summary>
    public string? DefaultsJson { get; private set; }

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
        Guid tenantId,
        Guid initiatedBy,
        LeadImportSourceType sourceType,
        string sourceReference,
        TimeProvider clock,
        string? originalFileName = null,
        string? defaultsJson = null)
        => new()
        {
            Id               = Guid.NewGuid(),
            TenantId         = tenantId,
            InitiatedBy      = initiatedBy,
            SourceType       = sourceType,
            SourceReference  = sourceReference,
            OriginalFileName = originalFileName,
            DefaultsJson     = defaultsJson,
            Status           = LeadImportStatus.Pending,
            CreatedAt        = clock.GetUtcNow()
        };

    public void MarkProcessing() => Status = LeadImportStatus.Processing;

    public void Complete(
        int totalRows, int succeeded, int skipped, int failed,
        string? failureDetailsJson, TimeProvider clock)
    {
        TotalRows          = totalRows;
        Succeeded          = succeeded;
        Skipped            = skipped;
        Failed             = failed;
        FailureDetailsJson = failureDetailsJson;
        Status             = LeadImportStatus.Completed;
        CompletedAt        = clock.GetUtcNow();
    }

    public void Fail(string error, TimeProvider clock)
    {
        Status       = LeadImportStatus.Failed;
        ErrorMessage = error;
        CompletedAt  = clock.GetUtcNow();
    }
}

public enum LeadImportStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum LeadImportSourceType
{
    File,
    GoogleSheet,
    GoogleContacts
}
