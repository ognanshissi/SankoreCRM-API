namespace Sankore.Modules.Customers.Features.Import.ImportFromGoogleSheet;

using Hangfire;
using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportClientsFromGoogleSheetHandler(
    CustomersDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportClientsFromGoogleSheetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        ImportClientsFromGoogleSheetCommand cmd, CancellationToken ct)
    {
        var job = ClientImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy, ClientImportSourceType.GoogleSheet, cmd.SpreadsheetUrl,
            clock, cmd.DefaultAgencyId);

        db.ClientImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessClientImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(job.Id);
    }
}
