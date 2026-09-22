namespace Sankore.Modules.Administration.Features.ImportUsers.ImportFromGoogleSheet;

using Hangfire;
using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportFromGoogleSheetHandler(
    AdministrationDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportFromGoogleSheetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ImportFromGoogleSheetCommand cmd, CancellationToken ct)
    {
        var job = UserImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            UserImportSourceType.GoogleSheet, cmd.SpreadsheetUrl, clock);

        db.UserImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessUserImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(job.Id);
    }
}
