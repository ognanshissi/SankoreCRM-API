namespace Sankore.Modules.Customers.Domain;

using System.Security.Cryptography;

/// <summary>
/// An asynchronous client export. The produced file is never served directly: the requester gets
/// a single-use, short-lived <see cref="DownloadToken"/> that expires at <see cref="ExpiresAt"/>
/// (<see cref="CustomerSettingKeys.ExportLinkTtlMinutes"/>), because the file contains personal data.
/// </summary>
public sealed class ClientExportJob
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequestedBy { get; private set; }

    /// <summary>JSON snapshot of the list filters the export must reproduce.</summary>
    public string FiltersJson { get; private set; } = default!;

    public ExportJobStatus Status { get; private set; }
    public int RowCount { get; private set; }

    /// <summary>Opaque storage reference of the generated file; null until completed.</summary>
    public string? FileReference { get; private set; }

    public string DownloadToken { get; private set; } = default!;
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? ErrorMessage { get; private set; }

    private ClientExportJob() { } // EF Core

    public static ClientExportJob Queue(Guid tenantId, Guid requestedBy, string filtersJson, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var lifetime = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(60);

        return new ClientExportJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedBy = requestedBy,
            FiltersJson = string.IsNullOrWhiteSpace(filtersJson) ? "{}" : filtersJson,
            Status = ExportJobStatus.Queued,
            RowCount = 0,
            // 192 bits of entropy: the token is the only thing protecting the file.
            DownloadToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            RequestedAt = now,
            ExpiresAt = now.Add(lifetime),
        };
    }

    public void Start() => Status = ExportJobStatus.Running;

    public void Complete(string fileReference, int rowCount, DateTimeOffset at)
    {
        Status = ExportJobStatus.Completed;
        FileReference = fileReference;
        RowCount = Math.Max(0, rowCount);
        CompletedAt = at;
        ErrorMessage = null;
    }

    public void Fail(string error, DateTimeOffset at)
    {
        Status = ExportJobStatus.Failed;
        ErrorMessage = string.IsNullOrWhiteSpace(error) ? "UNKNOWN" : error.Trim();
        CompletedAt = at;
    }

    public bool IsDownloadable(DateTimeOffset now) =>
        Status == ExportJobStatus.Completed
        && !string.IsNullOrWhiteSpace(FileReference)
        && now <= ExpiresAt;
}
