namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// One bulk client import: where the rows came from, how far it got, and what failed.
/// Mirrors the user import of module M12 so an operator meets the same flow twice.
/// </summary>
public sealed class ClientImportJob
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid InitiatedBy { get; private set; }

    public ClientImportSourceType SourceType { get; private set; }

    /// <summary>File-store key, or the spreadsheet id/URL. Opaque to this entity.</summary>
    public string SourceReference { get; private set; } = null!;

    public string? OriginalFileName { get; private set; }

    /// <summary>
    /// Agency every row without an AgencyCode of its own lands in. A client belongs to an
    /// agency, so an import with neither this nor a column cannot produce anything.
    /// </summary>
    public Guid? DefaultAgencyId { get; private set; }

    public ClientImportStatus Status { get; private set; }
    public int TotalRows { get; private set; }
    public int Succeeded { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }

    /// <summary>
    /// Per-row failures as JSON. Holds the row number and the reason — never the identity
    /// document or the phone number of the person the row was about.
    /// </summary>
    public string? FailureDetailsJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private ClientImportJob() { }

    public static ClientImportJob Create(
        Guid tenantId,
        Guid initiatedBy,
        ClientImportSourceType sourceType,
        string sourceReference,
        TimeProvider clock,
        Guid? defaultAgencyId = null,
        string? originalFileName = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InitiatedBy = initiatedBy,
            SourceType = sourceType,
            SourceReference = sourceReference,
            DefaultAgencyId = defaultAgencyId,
            OriginalFileName = originalFileName,
            Status = ClientImportStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };

    public void MarkProcessing() => Status = ClientImportStatus.Processing;

    public void Complete(
        int totalRows, int succeeded, int skipped, int failed,
        string? failureDetailsJson, TimeProvider clock)
    {
        TotalRows = totalRows;
        Succeeded = succeeded;
        Skipped = skipped;
        Failed = failed;
        FailureDetailsJson = failureDetailsJson;
        // "Completed" says the run finished, not that every row worked — the counters say that.
        Status = ClientImportStatus.Completed;
        CompletedAt = clock.GetUtcNow();
    }

    public void Fail(string error, TimeProvider clock)
    {
        Status = ClientImportStatus.Failed;
        ErrorMessage = error;
        CompletedAt = clock.GetUtcNow();
    }
}

/// <summary>
/// Google Contacts is deliberately absent, unlike the user import: a contact card carries a
/// name, a phone and an email, and none of the date of birth, identity document or agency a
/// client record needs — every row would be rejected.
/// </summary>
public enum ClientImportSourceType { File, GoogleSheet }

public enum ClientImportStatus { Pending, Processing, Completed, Failed }
