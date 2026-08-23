using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;

/// <param name="RootId">
/// Optional. When provided, returns the subtree rooted at that agency.
/// When null, returns the full tenant tree starting from all HQ / root agencies.
/// </param>
/// <param name="IncludeDeleted">Include soft-deleted agencies in the tree.</param>
public sealed record GetAgencyTreeQuery(
    Guid? RootId,
    bool IncludeDeleted = false
) : IRequest<Result<List<AgencyTreeNodeDto>>>;
