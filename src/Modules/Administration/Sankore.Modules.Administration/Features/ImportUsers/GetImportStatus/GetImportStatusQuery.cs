namespace Sankore.Modules.Administration.Features.ImportUsers.GetImportStatus;

using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

internal sealed record GetImportStatusQuery(Guid ImportJobId)
    : IRequest<Result<UserImportStatusDto>>;

public sealed record UserImportStatusDto(
    Guid Id,
    UserImportSourceType SourceType,
    UserImportStatus Status,
    int TotalRows,
    int Succeeded,
    int Skipped,
    int Failed,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
