using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.MoveAgency;

internal sealed class MoveAgencyHandler(
    AdministrationDbContext db
) : IRequestHandler<MoveAgencyCommand, Result>
{
    public async Task<Result> Handle(MoveAgencyCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies
            .AsTracking()
            .Where(a => a.Id == request.AgencyId)
            .SingleOrDefaultAsync(ct);

        if (agency is null)
            return Result.Fail($"Agency {request.AgencyId} not found.");

        if (agency.IsDeleted)
            return Result.Fail("Cannot move a deleted agency.");

        // Guard: an agency cannot be its own parent
        if (request.NewParentAgencyId == request.AgencyId)
            return Result.Fail("An agency cannot be its own parent.");

        // Validate new parent exists (when provided)
        if (request.NewParentAgencyId.HasValue)
        {
            var parentExists = await db.Agencies
                .AnyAsync(a => a.Id == request.NewParentAgencyId.Value && !a.IsDeleted, ct);

            if (!parentExists)
                return Result.Fail($"Parent agency {request.NewParentAgencyId} not found.");
        }

        // Guard against circular references: newParentId must not be a descendant of this agency
        if (request.NewParentAgencyId.HasValue)
        {
            var allAgencies = await db.Agencies
                .Select(a => new { a.Id, a.ParentAgencyId })
                .ToListAsync(ct);

            var descendants = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(request.AgencyId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var child in allAgencies.Where(a => a.ParentAgencyId == current))
                {
                    if (descendants.Add(child.Id))
                        queue.Enqueue(child.Id);
                }
            }

            if (descendants.Contains(request.NewParentAgencyId.Value))
                return Result.Fail("Cannot move an agency into one of its own descendants.");
        }

        agency.MoveTo(request.NewParentAgencyId);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
