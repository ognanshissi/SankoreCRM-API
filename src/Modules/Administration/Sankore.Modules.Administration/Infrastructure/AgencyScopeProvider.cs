namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Administration-side implementation of <see cref="IAgencyScopeProvider"/>:
/// Administration owns the agency tree, so it is the only module able to walk it.
///
/// Perimeter = the user's own agency + every ACTIVE descendant (BFS over
/// <c>Agency.ParentAgencyId</c>, same traversal as
/// <c>AdministrationModuleFacade.GetTeamAgentIdsAsync</c>) + every agency
/// delegated through an active <c>PermissionAttribution</c> with
/// <c>ScopeType == "Agency"</c> currently in its validity window (descendants of
/// those agencies included).
///
/// All queries use <c>IgnoreQueryFilters()</c> + an explicit TenantId predicate
/// so the provider also works from Hangfire jobs, where no JWT tenant exists.
/// </summary>
internal sealed class AgencyScopeProvider(AdministrationDbContext db) : IAgencyScopeProvider
{
    private const string AgencyScopeType = "Agency";

    public async Task<IReadOnlySet<Guid>?> GetAccessibleAgencyIdsAsync(
        Guid tenantId, Guid userId, CancellationToken ct)
    {
        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.Id == userId)
            .Select(u => new { u.IsSuperUser, u.AgencyId })
            .FirstOrDefaultAsync(ct);

        // Unknown user → sees nothing (an empty set is a denial; null would mean
        // "unrestricted", which must never be granted by accident).
        if (user is null)
            return new HashSet<Guid>();

        // Super-users and tenant-wide accounts (no agency) are unrestricted.
        if (user.IsSuperUser || user.AgencyId is null)
            return null;

        var accessible = new HashSet<Guid>();
        await AddWithDescendantsAsync(tenantId, user.AgencyId.Value, accessible, ct);

        var now = DateTimeOffset.UtcNow;
        var delegatedAgencyIds = await db.PermissionAttributions
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId
                        && p.UserId == userId
                        && p.IsActive
                        && p.ScopeType == AgencyScopeType
                        && p.ScopeId != null
                        && p.StartDate <= now
                        && p.EndDate >= now)
            .Select(p => p.ScopeId!.Value)
            .Distinct()
            .ToListAsync(ct);

        foreach (var agencyId in delegatedAgencyIds)
            await AddWithDescendantsAsync(tenantId, agencyId, accessible, ct);

        return accessible;
    }

    public async Task<bool> CanAccessAgencyAsync(
        Guid tenantId, Guid userId, Guid agencyId, CancellationToken ct)
    {
        var set = await GetAccessibleAgencyIdsAsync(tenantId, userId, ct);
        return set is null || set.Contains(agencyId);
    }

    /// <summary>
    /// Adds <paramref name="rootAgencyId"/> and every active descendant to
    /// <paramref name="accessible"/>. Already-visited nodes are skipped, so a
    /// delegated agency nested inside the user's own subtree costs nothing and a
    /// corrupted (cyclic) parent chain cannot loop forever.
    /// </summary>
    private async Task AddWithDescendantsAsync(
        Guid tenantId, Guid rootAgencyId, HashSet<Guid> accessible, CancellationToken ct)
    {
        if (!accessible.Add(rootAgencyId))
            return;

        var frontier = new Queue<Guid>();
        frontier.Enqueue(rootAgencyId);

        while (frontier.Count > 0)
        {
            var parentId = frontier.Dequeue();
            var children = await db.Agencies
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId && a.ParentAgencyId == parentId && a.IsActive)
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var childId in children)
            {
                if (accessible.Add(childId))
                    frontier.Enqueue(childId);
            }
        }
    }
}
