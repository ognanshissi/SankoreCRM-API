namespace Sankore.Modules.Administration.Features.Users.GetLoginHistory;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetLoginHistoryQuery(
    Guid UserId,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<IReadOnlyList<LoginHistoryDto>>>;

public sealed record LoginHistoryDto(
    Guid Id,
    DateTimeOffset OccuredAt,
    double? Latitude,
    double? Longitude);
