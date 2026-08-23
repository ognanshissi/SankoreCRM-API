using MediatR;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ListAgencies;

/// <param name="ParentAgencyId">Filter by parent. Pass null to list all; pass Guid.Empty to list root-level agencies.</param>
/// <param name="IncludeDeleted">Include soft-deleted agencies.</param>
/// <param name="Page">1-based page number. Defaults to 1.</param>
/// <param name="PageSize">Items per page. 0 = no pagination (return all). Defaults to 20.</param>
public sealed record ListAgenciesQuery(
    Guid? ParentAgencyId,
    bool IncludeDeleted = false,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<AgencyDto>>>;
