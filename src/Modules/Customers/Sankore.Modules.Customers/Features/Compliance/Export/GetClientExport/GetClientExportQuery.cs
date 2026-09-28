namespace Sankore.Modules.Customers.Features.Compliance.Export.GetClientExport;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Polls one export. A query: no <c>ICommand</c> — the audited act is requesting the export, not
/// watching it progress.
/// </summary>
public sealed record GetClientExportQuery(Guid ExportId)
    : IRequest<Result<ClientExportStatusDto>>;

/// <summary>
/// Status of an export plus, once it is ready, the one-shot download link.
/// <para>
/// <see cref="DownloadUrl"/> embeds the download token, so it is returned only to the requester
/// and only while the link is live: everyone else gets <c>EXPORT_NOT_FOUND</c> from the handler
/// rather than a URL they could replay.
/// </para>
/// </summary>
public sealed record ClientExportStatusDto(
    Guid ExportId,
    string Status,
    int RowCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired,
    string? DownloadUrl,
    string? ErrorMessage);
