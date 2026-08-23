using MediatR;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ListAgencies;

/// <param name="ParentAgencyId">Filter by parent. Pass null to list all; pass Guid.Empty to list root-level agencies.</param>
/// <param name="IncludeDeleted">Include soft-deleted agencies.</param>
public sealed record ListAgenciesQuery(
    Guid? ParentAgencyId,
    bool IncludeDeleted = false
) : IRequest<Result<List<AgencyDto>>>;
