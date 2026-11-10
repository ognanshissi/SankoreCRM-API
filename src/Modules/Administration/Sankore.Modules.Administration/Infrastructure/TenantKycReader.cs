namespace Sankore.Modules.Administration.Infrastructure;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// <see cref="ITenantKyc"/> off <c>compliance_settings.kyc_enabled</c> and the tenant's modules
/// (docs/kyc-par-institution.md).
///
/// <para>
/// The switch is cached a minute per TEAM tenant (IDistributedCache optional, as the product
/// catalogue's) and dropped by <see cref="InvalidateAsync"/> on every change from this host. The
/// modules come from the cached <see cref="TenantInfo"/>, so a module approved by the platform
/// keeps KYC in force within that cache's own delay, before the switch itself is put back on.
/// </para>
///
/// <para>
/// <b>Fails closed.</b> A switch that cannot be read is ON: a gate that cannot tell must not waive
/// a customer's verification.
/// </para>
/// </summary>
internal sealed class TenantKycReader(
    AdministrationDbContext db,
    ITenantStore store,
    ILogger<TenantKycReader> logger,
    IDistributedCache? cache = null) : ITenantKyc
{
    private static readonly DistributedCacheEntryOptions Options = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
    };

    internal static string CacheKey(Guid teamTenantId) => $"m12:tenant-kyc:{teamTenantId}";

    public async Task<TenantKycState> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var info = await store.GetAsync(tenantId, ct);
            // A twin reads its production's switch; the modules are already the parent's
            // (TenantProjection), and an unknown tenant reads as having them all.
            var team = info?.TeamTenantId ?? TenantTwins.TeamOf(tenantId);
            var module = TenantKycExtensions.RegulatedModuleIn(info?.ActiveModules);

            return new TenantKycState(await SwitchedOnAsync(team, ct), module);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "KYC switch of tenant {TenantId} not read: read as in force", tenantId);
            return TenantKycState.InForceByDefault;
        }
    }

    private async Task<bool> SwitchedOnAsync(Guid teamTenantId, CancellationToken ct)
    {
        var key = CacheKey(teamTenantId);
        if (cache is not null)
        {
            try
            {
                var hit = await cache.GetStringAsync(key, ct);
                if (hit is not null) return hit != "0";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cache is an optimisation: an unreachable Redis costs a query.
            }
        }

        // No row = the platform default, ON. IgnoreQueryFilters: jobs and consumers read this with
        // no ambient tenant, and a twin reads its parent's row. Outside the caller's ambient
        // transaction: Customers, Leads and M02 ask from inside their own commands, and a context
        // of this module enlisted there fails the command (« connection used with an ambient
        // transaction »).
        bool? row;
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            row = await db.ComplianceSettings
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == teamTenantId)
                .Select(s => (bool?)s.KycEnabled)
                .FirstOrDefaultAsync(ct);
            suppressed.Complete();
        }
        var on = row ?? true;

        if (cache is not null)
        {
            try { await cache.SetStringAsync(key, on ? "1" : "0", Options, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        return on;
    }

    internal async Task InvalidateAsync(Guid teamTenantId, CancellationToken ct)
    {
        if (cache is null) return;
        try { await cache.RemoveAsync(CacheKey(teamTenantId), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "KYC switch cache of tenant {TenantId} not dropped", teamTenantId);
        }
    }
}
