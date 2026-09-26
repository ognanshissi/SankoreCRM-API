namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleContacts;

using Hangfire;
using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ImportFromGoogleContactsHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire,
    TimeProvider clock)
    : IRequestHandler<ImportFromGoogleContactsCommand, Result<ImportLeadsAccepted>>
{
    public const string SourceMarker = "google-contacts";

    public async Task<Result<ImportLeadsAccepted>> Handle(
        ImportFromGoogleContactsCommand cmd, CancellationToken ct)
    {
        var job = LeadImportJob.Create(
            cmd.TenantId, cmd.InitiatedBy,
            LeadImportSourceType.GoogleContacts, SourceMarker, clock,
            defaultsJson: ImportDefaultsSerializer.Serialize(cmd.Defaults));

        db.LeadImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessLeadImportJob>(
            j => j.ExecuteAsync(job.Id, cmd.TenantId, cmd.InitiatedBy));

        return Result.Ok(new ImportLeadsAccepted(job.Id));
    }
}
