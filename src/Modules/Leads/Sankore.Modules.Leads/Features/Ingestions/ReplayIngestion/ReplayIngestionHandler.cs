namespace Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ReplayIngestionHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire)
    : IRequestHandler<ReplayIngestionCommand, Result>
{
    public async Task<Result> Handle(ReplayIngestionCommand cmd, CancellationToken ct)
    {
        var ingestion = await db.LeadIngestions
            .AsTracking()
            .FirstOrDefaultAsync(i => i.Id == cmd.IngestionId, ct);

        if (ingestion is null)
            return Result.Fail("INGESTION_NOT_FOUND");

        if (ingestion.Status is not (LeadIngestionStatus.Rejected or LeadIngestionStatus.Failed))
            return Result.Fail("ONLY_REJECTED_OR_FAILED_CAN_BE_REPLAYED");

        // Enqueue a background job with only the ingestion ID (opaque payload)
        hangfire.Enqueue<ReplayIngestionJob>(
            j => j.ExecuteAsync(ingestion.Id, ingestion.TenantId));

        return Result.Ok();
    }
}
