namespace Sankore.Modules.Customers.Features.Import.GetImportStatus;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetClientImportStatusQuery(Guid ImportJobId)
    : IRequest<Result<ClientImportStatusDto>>;

public sealed record ClientImportStatusDto(
    Guid Id,
    string Status,
    string SourceType,
    string? OriginalFileName,
    int TotalRows,
    int Succeeded,
    int Skipped,
    int Failed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    /// <summary>Row number and reason for each rejected row. Never the person's identifiers.</summary>
    IReadOnlyList<ClientImportRowFailure> Failures);

public sealed record ClientImportRowFailure(int RowNumber, string Reference, string Error);
