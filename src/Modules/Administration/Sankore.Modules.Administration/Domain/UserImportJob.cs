namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;

public sealed class UserImportJob : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid InitiatedBy { get; private set; }

    public UserImportSourceType SourceType { get; private set; }

    /// <summary>
    /// Opaque reference: file key for File source, spreadsheet URL for GoogleSheet,
    /// or "google-contacts" marker for GoogleContacts.
    /// </summary>
    public string SourceReference { get; private set; } = null!;

    public string? OriginalFileName { get; private set; }
    public UserImportStatus Status { get; private set; }
    public int TotalRows { get; private set; }
    public int Succeeded { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string? FailureDetailsJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private UserImportJob() { }

    public static UserImportJob Create(
        Guid tenantId,
        Guid initiatedBy,
        UserImportSourceType sourceType,
        string sourceReference,
        TimeProvider clock,
        string? originalFileName = null)
        => new()
        {
            Id               = Guid.NewGuid(),
            TenantId         = tenantId,
            InitiatedBy      = initiatedBy,
            SourceType       = sourceType,
            SourceReference  = sourceReference,
            OriginalFileName = originalFileName,
            Status           = UserImportStatus.Pending,
            CreatedAt        = clock.GetUtcNow()
        };

    public void MarkProcessing() => Status = UserImportStatus.Processing;

    public void Complete(
        int totalRows, int succeeded, int skipped, int failed,
        string? failureDetailsJson, TimeProvider clock)
    {
        TotalRows          = totalRows;
        Succeeded          = succeeded;
        Skipped            = skipped;
        Failed             = failed;
        FailureDetailsJson = failureDetailsJson;
        Status             = UserImportStatus.Completed;
        CompletedAt        = clock.GetUtcNow();
    }

    public void Fail(string error, TimeProvider clock)
    {
        Status       = UserImportStatus.Failed;
        ErrorMessage = error;
        CompletedAt  = clock.GetUtcNow();
    }
}

public enum UserImportStatus { Pending, Processing, Completed, Failed }

public enum UserImportSourceType { File, GoogleSheet, GoogleContacts }
