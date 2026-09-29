namespace Sankore.Modules.Customers.Features.Import.ImportFromFile;

using Hangfire;
using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportClientsFromFileHandler(
    CustomersDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportClientsFromFileCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ImportClientsFromFileCommand cmd, CancellationToken ct)
    {
        var job = ClientImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy, ClientImportSourceType.File, cmd.FileReference,
            clock, cmd.DefaultAgencyId, cmd.OriginalFileName);

        db.ClientImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        // Enqueued, not run inline: a file of several hundred clients must not hold an HTTP
        // request open, and the caller polls the status endpoint.
        hangfire.Enqueue<ProcessClientImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(job.Id);
    }
}
