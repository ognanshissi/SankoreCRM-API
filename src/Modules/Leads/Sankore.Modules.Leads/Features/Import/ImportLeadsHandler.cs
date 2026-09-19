namespace Sankore.Modules.Leads.Features.Import;

using Hangfire;
using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates a <see cref="LeadImportJob"/> tracking entity, then enqueues
/// a Hangfire background job. Returns immediately with the job ID.
/// </summary>
internal sealed class ImportLeadsHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportLeadsCommand, Result<ImportLeadsAccepted>>
{
    public async Task<Result<ImportLeadsAccepted>> Handle(
        ImportLeadsCommand cmd, CancellationToken ct)
    {
        var importJob = LeadImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            cmd.FileReference, cmd.OriginalFileName, clock);

        db.LeadImportJobs.Add(importJob);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessLeadImportJob>(
            job => job.ExecuteAsync(importJob.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(new ImportLeadsAccepted(importJob.Id));
    }
}
