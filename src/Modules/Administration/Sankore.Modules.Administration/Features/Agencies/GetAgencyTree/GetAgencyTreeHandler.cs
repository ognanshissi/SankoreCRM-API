using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;

internal sealed class GetAgencyTreeHandler(
    AdministrationDbContext db
) : IRequestHandler<GetAgencyTreeQuery, Result<List<AgencyTreeNodeDto>>>
{
    public async Task<Result<List<AgencyTreeNodeDto>>> Handle(
        GetAgencyTreeQuery request, CancellationToken ct)
    {
        // Single query — fetch every agency for the tenant, then build the tree in memory.
        // The global query filter on TenantId is already applied by EF.
        var query = db.Agencies.AsQueryable();

        if (!request.IncludeDeleted)
            query = query.Where(a => !a.IsDeleted);

        var flat = await query
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Code,
                a.Description,
                AgencyType = a.AgencyType.ToString(),
                a.ParentAgencyId,
                a.IsHeadQuarterAgency,
                a.IsActive,
            })
            .ToListAsync(ct);

        // When a rootId is supplied, verify it exists before anything else.
        if (request.RootId.HasValue && flat.All(a => a.Id != request.RootId.Value))
            return Result.Fail<List<AgencyTreeNodeDto>>(
                $"Agency {request.RootId.Value} not found.");

        if (flat.Count == 0)
            return Result.Ok(new List<AgencyTreeNodeDto>());

        // Index children by parentId for O(n) tree construction.
        var byParent = flat.ToLookup(a => a.ParentAgencyId);

        AgencyTreeNodeDto BuildNode(Guid id)
        {
            var row = flat.First(a => a.Id == id);
            var children = byParent[id]
                .Select(c => BuildNode(c.Id))
                .ToList();

            return new AgencyTreeNodeDto(
                row.Id,
                row.Name,
                row.Code,
                row.Description,
                row.AgencyType,
                row.ParentAgencyId,
                row.IsHeadQuarterAgency,
                row.IsActive,
                ChildCount: children.Count,
                Children: children);
        }

        List<AgencyTreeNodeDto> roots;

        if (request.RootId.HasValue)
        {
            // Return a single-root list containing the requested subtree.
            roots = [BuildNode(request.RootId.Value)];
        }
        else
        {
            // All agencies with no parent are the forest roots.
            roots = byParent[null]
                .Select(a => BuildNode(a.Id))
                .ToList();
        }

        return Result.Ok(roots);
    }
}
