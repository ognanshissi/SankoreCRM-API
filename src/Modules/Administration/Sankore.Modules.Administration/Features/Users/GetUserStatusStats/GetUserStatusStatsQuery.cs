using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserStatusStats;

public sealed record GetUserStatusStatsQuery : IRequest<Result<UserStatusStatsDto>>;

public sealed record UserStatusStatsDto(
    int Total,
    int Active,
    int PendingActivation,
    int Disabled,
    int Locked);
