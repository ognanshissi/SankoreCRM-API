namespace Sankore.Modules.Leads.Features.Ingestions.GetIngestionPayload;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetIngestionPayloadHandler(LeadsDbContext db)
    : IRequestHandler<GetIngestionPayloadCommand, Result<IngestionPayloadDto>>
{
    public async Task<Result<IngestionPayloadDto>> Handle(
        GetIngestionPayloadCommand cmd, CancellationToken ct)
    {
        var ingestion = await db.LeadIngestions
            .Where(i => i.Id == cmd.IngestionId)
            .Select(i => new IngestionPayloadDto(i.Id, i.RawPayloadJson))
            .FirstOrDefaultAsync(ct);

        return ingestion is null
            ? Result.Fail<IngestionPayloadDto>("INGESTION_NOT_FOUND")
            : Result.Ok(ingestion);
    }
}
