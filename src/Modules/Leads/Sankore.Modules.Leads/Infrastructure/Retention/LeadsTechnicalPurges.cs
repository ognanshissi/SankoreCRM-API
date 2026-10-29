namespace Sankore.Modules.Leads.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>The file a lead import read and the detail of its rejected rows — see UserImportFilesPurge.</summary>
internal sealed class LeadImportFilesPurge(
    LeadsDbContext db, IFileStore files, ILogger<LeadImportFilesPurge> logger) : ITechnicalPurge
{
    public string Name => "leads.lead_import_jobs";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ImportFiles;
        var finished = db.LeadImportJobs.IgnoreQueryFilters()
            .Where(j => j.Status != LeadImportStatus.Pending && j.Status != LeadImportStatus.Processing
                     && (j.CompletedAt ?? j.CreatedAt) < before);

        var withFile = await finished
            .Where(j => j.SourceType == LeadImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference).ToListAsync(ct);
        // A List, not the set: on .NET 10 a set's Contains does not translate.
        var deleted = (await TechnicalFiles.DeleteAsync(files, withFile, logger, ct)).ToList();

        var gone = await finished.Where(j => deleted.Contains(j.SourceReference))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceReference, ""), ct);
        var details = await finished.Where(j => j.FailureDetailsJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.FailureDetailsJson, (string?)null), ct);
        return gone + details;
    }
}

/// <summary>
/// What a source sent for a lead, exactly as received — a second copy of the prospect's data next
/// to the lead, which holds it now. The row stays (which source, which run, accepted or why not);
/// only the payload goes.
/// </summary>
internal sealed class RawIngestionsPurge(LeadsDbContext db) : ITechnicalPurge
{
    public string Name => "leads.lead_ingestions";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.RawIngestions;
        return db.LeadIngestions.IgnoreQueryFilters()
            .Where(i => i.IngestedAt < before && i.RawPayloadJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RawPayloadJson, (string?)null), ct);
    }
}

/// <summary>What a partner reported about a lead (webhooks lot 6), purged like an ingestion's payload.</summary>
internal sealed class RawExternalEventsPurge(LeadsDbContext db) : ITechnicalPurge
{
    public string Name => "leads.lead_external_events";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.RawIngestions;
        return db.LeadExternalEvents.IgnoreQueryFilters()
            .Where(e => e.ReceivedAt < before && e.PayloadJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadJson, (string?)null), ct);
    }
}
