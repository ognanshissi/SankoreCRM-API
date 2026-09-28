namespace Sankore.Modules.Customers.Features.Groups.GetGroup;

using MediatR;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// Group detail with its membership roll (US-M01-BE-19).
/// </summary>
/// <param name="IncludeFormerMembers">
/// When true, memberships closed by a removal or a dissolution are returned too
/// (they are never deleted), each carrying its <c>LeftAt</c> and
/// <c>LeaveReason</c>.
/// </param>
public sealed record GetGroupQuery(Guid GroupId, bool IncludeFormerMembers = false)
    : IRequest<Result<GroupDetailDto>>;

/// <summary>
/// <paramref name="Version"/> is the xmin concurrency token: echo it back as
/// <c>expectedVersion</c> on any mutating call to detect a concurrent edit.
/// </summary>
public sealed record GroupDetailDto(
    Guid Id,
    string Name,
    string Type,
    string Status,
    Guid AgencyId,
    DateOnly ConstitutionDate,
    string? DissolutionReason,
    DateTimeOffset? DissolvedAt,
    int ActiveMemberCount,
    int MinimumSize,
    int MaximumSize,
    Guid? PresidentClientId,
    Guid? TreasurerClientId,
    Guid? SecretaryClientId,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<GroupMemberDto> Members);
