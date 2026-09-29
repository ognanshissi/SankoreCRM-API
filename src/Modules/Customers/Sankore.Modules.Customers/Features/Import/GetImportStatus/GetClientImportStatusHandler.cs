namespace Sankore.Modules.Customers.Features.Import.GetImportStatus;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetClientImportStatusHandler(CustomersDbContext db)
    : IRequestHandler<GetClientImportStatusQuery, Result<ClientImportStatusDto>>
{
    public async Task<Result<ClientImportStatusDto>> Handle(
        GetClientImportStatusQuery query, CancellationToken ct)
    {
        var job = await db.ClientImportJobs
            .FirstOrDefaultAsync(j => j.Id == query.ImportJobId, ct);

        if (job is null)
            return Result.Fail<ClientImportStatusDto>("IMPORT_JOB_NOT_FOUND");

        var failures = job.FailureDetailsJson is null
            ? []
            : JsonSerializer.Deserialize<List<ClientImportRowFailure>>(job.FailureDetailsJson) ?? [];

        return Result.Ok(new ClientImportStatusDto(
            job.Id,
            job.Status.ToString(),
            job.SourceType.ToString(),
            job.OriginalFileName,
            job.TotalRows,
            job.Succeeded,
            job.Skipped,
            job.Failed,
            job.CreatedAt,
            job.CompletedAt,
            job.ErrorMessage,
            failures));
    }
}
