namespace Sankore.Modules.Administration.Features.ImportUsers.ImportFromFile;

using Hangfire;
using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportFromFileHandler(
    AdministrationDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportFromFileCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ImportFromFileCommand cmd, CancellationToken ct)
    {
        var job = UserImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            UserImportSourceType.File, cmd.FileReference, clock, cmd.OriginalFileName);

        db.UserImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessUserImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(job.Id);
    }
}
