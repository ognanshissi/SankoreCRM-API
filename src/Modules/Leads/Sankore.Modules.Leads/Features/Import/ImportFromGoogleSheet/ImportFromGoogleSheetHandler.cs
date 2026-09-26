namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleSheet;

using Hangfire;
using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportFromGoogleSheetHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportFromGoogleSheetCommand, Result<ImportLeadsAccepted>>
{
    public async Task<Result<ImportLeadsAccepted>> Handle(
        ImportFromGoogleSheetCommand cmd, CancellationToken ct)
    {
        var job = LeadImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            LeadImportSourceType.GoogleSheet, cmd.SpreadsheetUrl, clock,
            defaultsJson: ImportDefaultsSerializer.Serialize(cmd.Defaults));

        db.LeadImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessLeadImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(new ImportLeadsAccepted(job.Id));
    }
}
