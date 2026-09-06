using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.GetUser;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ListUsers;

public sealed record ListUsersQuery(
    UserStatus? Status,
    Guid? AgencyId,
    string? Search,
    int Page,
    int PageSize
) : IRequest<Result<ListUsersResult>>;

public sealed record ListUsersResult(
    List<UserDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
