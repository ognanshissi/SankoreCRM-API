namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentDistribution;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetSegmentDistributionHandler(
    CustomersDbContext db,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser)
    : IRequestHandler<GetSegmentDistributionQuery, Result<SegmentDistributionDto>>
{
    public async Task<Result<SegmentDistributionDto>> Handle(
        GetSegmentDistributionQuery request, CancellationToken ct)
    {
        var query = db.Clients
            .Where(c => c.Status != ClientStatus.Archived && c.Status != ClientStatus.Merged);

        // null = unrestricted (super-user); an empty set means the user sees nothing, which
        // must yield an empty distribution rather than the whole tenant.
        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (accessible is not null)
        {
            var ids = accessible.ToList();
            query = query.Where(c => ids.Contains(c.AgencyId));
        }

        var raw = await query
            .GroupBy(c => c.SegmentCode)
            .Select(g => new { SegmentCode = g.Key, ClientCount = g.Count() })
            .ToListAsync(ct);

        var total = raw.Sum(r => r.ClientCount);

        var buckets = raw
            // Largest segment first; the unsegmented bucket is pushed to the end, where it
            // reads as a backlog rather than as a segment of its own.
            .OrderBy(r => r.SegmentCode is null)
            .ThenByDescending(r => r.ClientCount)
            .ThenBy(r => r.SegmentCode, StringComparer.Ordinal)
            .Select(r => new SegmentBucketDto(
                r.SegmentCode,
                r.ClientCount,
                total == 0 ? 0m : Math.Round(r.ClientCount * 100m / total, 1)))
            .ToList();

        return Result.Ok(new SegmentDistributionDto(total, buckets));
    }
}
