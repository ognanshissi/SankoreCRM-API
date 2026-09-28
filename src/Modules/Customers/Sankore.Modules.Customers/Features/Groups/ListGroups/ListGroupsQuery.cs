namespace Sankore.Modules.Customers.Features.Groups.ListGroups;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Paginated group listing (US-M01-BE-19). A query, so it deliberately does NOT
/// implement <c>ICommand</c>: no transaction, no audit entry.
/// </summary>
/// <param name="Type">Filter by group type; null = every type.</param>
/// <param name="Status">Filter by lifecycle status; null = every status, dissolved groups included.</param>
/// <param name="AgencyId">
/// Restrict to one agency. Intersected with the caller's perimeter: asking for an
/// agency outside it yields an empty page rather than a 403.
/// </param>
/// <param name="Search">Case-insensitive substring match on the group name; ignored when blank.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Items per page, clamped to 1..200.</param>
public sealed record ListGroupsQuery(
    GroupType? Type = null,
    GroupStatus? Status = null,
    Guid? AgencyId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<GroupListItemDto>>>;

public sealed record GroupListItemDto(
    Guid Id,
    string Name,
    string Type,
    string Status,
    Guid AgencyId,
    DateOnly ConstitutionDate,
    int ActiveMemberCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
