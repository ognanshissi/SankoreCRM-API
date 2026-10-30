using System.Collections.Concurrent;

namespace Sankore.Shared.Kernel;

/// <summary>
/// Which tenants are test twins, and of whom (sandbox lot 2) — remembered from every
/// <see cref="TenantInfo"/> the tenant store hands out.
///
/// <para>
/// <b>Why a process-wide memory and not a lookup.</b> <see cref="ITenantContext.TeamTenantId"/> is
/// read synchronously, inside EF query filters and in jobs and consumers that have no request, and
/// the store is asynchronous. Every path that reaches a twin's data has read its record first: the
/// request pipeline resolves the tenant on every call, and the seeders and orchestrator jobs list
/// the active tenants — twins included — at start-up and on every run. A miss reads the twin as its
/// own team, which finds no user: a visible failure in test mode, never a leak into live data.
/// Callers able to await should prefer the store (<see cref="TenantInfo.TeamTenantId"/>).
/// </para>
/// </summary>
public static class TenantTwins
{
    private static readonly ConcurrentDictionary<Guid, Guid> Parents = new();

    public static void Remember(TenantInfo? tenant)
    {
        if (tenant?.ParentTenantId is { } parent) Parents[tenant.Id] = parent;
    }

    /// <summary>The live tenant of a known twin; any other id unchanged.</summary>
    public static Guid TeamOf(Guid tenantId) => Parents.TryGetValue(tenantId, out var parent) ? parent : tenantId;

    public static bool IsTwin(Guid tenantId) => Parents.ContainsKey(tenantId);
}
