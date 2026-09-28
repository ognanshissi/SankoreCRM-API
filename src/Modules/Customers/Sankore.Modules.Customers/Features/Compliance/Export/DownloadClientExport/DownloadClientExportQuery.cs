namespace Sankore.Modules.Customers.Features.Compliance.Export.DownloadClientExport;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Serves the generated CSV against a valid, unexpired download token.
/// <para>
/// A query, not an <c>ICommand</c>: the audited act is the export request, which already records
/// who asked for what. (Were a per-download audit line ever required, this would become an
/// <c>ICommand</c> — nothing else about the slice would change.)
/// </para>
/// </summary>
public sealed record DownloadClientExportQuery(Guid ExportId, string Token)
    : IRequest<Result<ClientExportFileDto>>;

/// <summary>The file, ready to be streamed back.</summary>
public sealed record ClientExportFileDto(byte[] Content, string FileName, string ContentType, int RowCount);
