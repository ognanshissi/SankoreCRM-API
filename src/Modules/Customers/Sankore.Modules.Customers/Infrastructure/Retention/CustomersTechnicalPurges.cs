namespace Sankore.Modules.Customers.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>Idempotency rows of messages consumed long ago: no broker redelivers that late.</summary>
internal sealed class CustomersInboxPurge(CustomersDbContext db) : ITechnicalPurge
{
    public string Name => "customers.inbox_messages";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ProcessedMessages;
        return db.InboxMessages.IgnoreQueryFilters().Where(m => m.ReceivedAt < before).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// The CSV of a client export whose download link has expired. Before, <c>ExpiresAt</c> only
/// invalidated the token: the file — every client of the selection — stayed in the store. The row
/// stays, its <c>FileReference</c> emptied.
/// </summary>
internal sealed class ExpiredExportFilesPurge(
    CustomersDbContext db, IFileStore files, ILogger<ExpiredExportFilesPurge> logger) : ITechnicalPurge
{
    public string Name => "customers.client_export_jobs";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = db.ClientExportJobs.IgnoreQueryFilters()
            .Where(j => j.ExpiresAt < now && j.FileReference != null);

        var references = await expired.Select(j => j.FileReference!).ToListAsync(ct);
        // A List, not the set: on .NET 10 a set's Contains does not translate.
        var deleted = (await TechnicalFiles.DeleteAsync(files, references, logger, ct)).ToList();

        return await expired.Where(j => deleted.Contains(j.FileReference!))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.FileReference, (string?)null), ct);
    }
}

/// <summary>The file a client import read and the detail of its rejected rows — see UserImportFilesPurge.</summary>
internal sealed class ClientImportFilesPurge(
    CustomersDbContext db, IFileStore files, ILogger<ClientImportFilesPurge> logger) : ITechnicalPurge
{
    public string Name => "customers.client_import_jobs";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ImportFiles;
        var finished = db.ClientImportJobs.IgnoreQueryFilters()
            .Where(j => j.Status != ClientImportStatus.Pending && j.Status != ClientImportStatus.Processing
                     && (j.CompletedAt ?? j.CreatedAt) < before);

        var withFile = await finished
            .Where(j => j.SourceType == ClientImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference).ToListAsync(ct);
        var deleted = (await TechnicalFiles.DeleteAsync(files, withFile, logger, ct)).ToList();

        var gone = await finished.Where(j => deleted.Contains(j.SourceReference))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceReference, ""), ct);
        var details = await finished.Where(j => j.FailureDetailsJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.FailureDetailsJson, (string?)null), ct);
        return gone + details;
    }
}
