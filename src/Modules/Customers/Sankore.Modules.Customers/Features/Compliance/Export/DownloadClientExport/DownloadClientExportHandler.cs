namespace Sankore.Modules.Customers.Features.Compliance.Export.DownloadClientExport;

using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class DownloadClientExportHandler(
    CustomersDbContext db,
    IFileStore fileStore,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<DownloadClientExportHandler> logger
) : IRequestHandler<DownloadClientExportQuery, Result<ClientExportFileDto>>
{
    public async Task<Result<ClientExportFileDto>> Handle(
        DownloadClientExportQuery query, CancellationToken ct)
    {
        var job = await db.ClientExportJobs
            .FirstOrDefaultAsync(j => j.Id == query.ExportId, ct);

        if (job is null)
            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportNotFound);

        // Same rule as the status endpoint: the file belongs to the person the audit entry names.
        if (job.RequestedBy != currentUser.Id)
            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportNotFound);

        // Constant-time comparison. A byte-by-byte '==' on a secret leaks how many leading
        // characters were right, which is enough to walk a token one character at a time.
        if (!TokensMatch(job.DownloadToken, query.Token))
        {
            logger.LogWarning(
                "Rejected download of client export {ExportId} (tenant {TenantId}): invalid token.",
                job.Id, job.TenantId);

            // Deliberately EXPORT_NOT_FOUND and not "invalid token": a distinct answer would
            // confirm the export id and turn this endpoint into an oracle.
            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportNotFound);
        }

        var now = clock.GetUtcNow();

        // Expiry is reported distinctly — the caller proved they hold the token, so telling them
        // the link aged out reveals nothing and saves a support ticket.
        if (now > job.ExpiresAt)
            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportLinkExpired);

        if (!job.IsDownloadable(now) || job.FileReference is null)
            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportNotFound);

        byte[] content;
        try
        {
            await using var stream = await fileStore.ReadAsync(job.FileReference, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The row says Completed but the blob is gone (retention sweep, storage migration).
            logger.LogError(ex,
                "Client export {ExportId} references missing file {FileReference}.",
                job.Id, job.FileReference);

            return Result.Fail<ClientExportFileDto>(CustomerErrors.ExportNotFound);
        }

        logger.LogInformation(
            "Client export {ExportId} ({RowCount} row(s)) downloaded by {UserId}.",
            job.Id, job.RowCount, currentUser.Id);

        return Result.Ok(new ClientExportFileDto(
            Content: content,
            FileName: ClientExportCsv.FileName(job.Id),
            ContentType: ClientExportCsv.ContentType,
            RowCount: job.RowCount));
    }

    private static bool TokensMatch(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);

        // FixedTimeEquals requires equal lengths; a length mismatch is an immediate mismatch and
        // leaks only the length, which the token format already makes public.
        return expectedBytes.Length == suppliedBytes.Length
               && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
