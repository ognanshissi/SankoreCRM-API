namespace Sankore.Modules.Customers.Features.Compliance.Export.GetClientExport;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetClientExportHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock
) : IRequestHandler<GetClientExportQuery, Result<ClientExportStatusDto>>
{
    public async Task<Result<ClientExportStatusDto>> Handle(
        GetClientExportQuery query, CancellationToken ct)
    {
        var job = await db.ClientExportJobs
            .FirstOrDefaultAsync(j => j.Id == query.ExportId, ct);

        if (job is null)
            return Result.Fail<ClientExportStatusDto>(CustomerErrors.ExportNotFound);

        // An export is private to whoever asked for it, even between two holders of
        // customers:export. The response carries the download token, and that token is a bearer
        // capability over a file full of personal data — handing it to a colleague would silently
        // widen the egress beyond the person the audit entry names.
        if (job.RequestedBy != currentUser.Id)
            return Result.Fail<ClientExportStatusDto>(CustomerErrors.ExportNotFound);

        var now = clock.GetUtcNow();
        var downloadable = job.IsDownloadable(now);

        return Result.Ok(new ClientExportStatusDto(
            ExportId: job.Id,
            Status: job.Status.ToString(),
            RowCount: job.RowCount,
            RequestedAt: job.RequestedAt,
            CompletedAt: job.CompletedAt,
            ExpiresAt: job.ExpiresAt,
            IsExpired: now > job.ExpiresAt,
            DownloadUrl: downloadable
                ? $"/api/v1/clients/exports/{job.Id:D}/download?token={Uri.EscapeDataString(job.DownloadToken)}"
                : null,
            ErrorMessage: job.ErrorMessage));
    }
}
