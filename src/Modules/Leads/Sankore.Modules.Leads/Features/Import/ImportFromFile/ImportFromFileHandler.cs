namespace Sankore.Modules.Leads.Features.Import.ImportFromFile;

using Hangfire;
using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportFromFileHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportFromFileCommand, Result<ImportLeadsAccepted>>
{
    public async Task<Result<ImportLeadsAccepted>> Handle(
        ImportFromFileCommand cmd, CancellationToken ct)
    {
        var job = LeadImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            LeadImportSourceType.File, cmd.FileReference, clock,
            originalFileName: cmd.OriginalFileName,
            defaultsJson: ImportDefaultsSerializer.Serialize(cmd.Defaults));

        db.LeadImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessLeadImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(new ImportLeadsAccepted(job.Id));
    }
}
