namespace Sankore.Modules.Administration.Features.ImportUsers.GetImportStatus;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetImportStatusHandler(AdministrationDbContext db)
    : IRequestHandler<GetImportStatusQuery, Result<UserImportStatusDto>>
{
    public async Task<Result<UserImportStatusDto>> Handle(
        GetImportStatusQuery query, CancellationToken ct)
    {
        var dto = await db.UserImportJobs
            .Where(j => j.Id == query.ImportJobId)
            .Select(j => new UserImportStatusDto(
                j.Id, j.SourceType, j.Status,
                j.TotalRows, j.Succeeded, j.Skipped, j.Failed,
                j.ErrorMessage, j.CreatedAt, j.CompletedAt))
            .FirstOrDefaultAsync(ct);

        return dto is null
            ? Result.Fail<UserImportStatusDto>("IMPORT_JOB_NOT_FOUND")
            : Result.Ok(dto);
    }
}
