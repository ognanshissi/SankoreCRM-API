using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>Reads modules off the cached <see cref="TenantInfo"/>. Scoped: it follows the request's tenant.</summary>
internal sealed class TenantModulesService(ITenantStore store, ITenantContext tenant) : ITenantModules
{
    public Task<bool> IsEnabledAsync(string module, CancellationToken ct = default)
        => IsEnabledAsync(tenant.CurrentTenantId, module, ct);

    public async Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct = default)
        => (await store.GetAsync(tenantId, ct))?.HasModule(module) ?? true;

    public async Task<IReadOnlyList<string>> ActiveAsync(Guid tenantId, CancellationToken ct = default)
        => (await store.GetAsync(tenantId, ct))?.ActiveModules ?? TenantModules.All;

    public Task<bool> IsReadableAsync(string module, CancellationToken ct = default)
        => IsReadableAsync(tenant.CurrentTenantId, module, ct);

    public async Task<bool> IsReadableAsync(Guid tenantId, string module, CancellationToken ct = default)
        => (await store.GetAsync(tenantId, ct))?.CanRead(module) ?? true;

    public async Task<IReadOnlyList<string>> ReadOnlyAsync(Guid tenantId, CancellationToken ct = default)
    {
        var info = await store.GetAsync(tenantId, ct);
        // Unknown modules read as « all active », so nothing is read-only.
        return info?.ActiveModules is null ? [] : info.ReadOnlyModules ?? [];
    }
}

/// <summary>
/// Drops a tenant's cached <see cref="TenantInfo"/>, so the next read goes to the registry.
/// Called by whoever has just changed the tenant in the registry from this host — a module
/// switched off must stop working on the next request, not 30 seconds later. Changes made
/// elsewhere (the admin front-end) still reach this host within the cache's TTL.
/// </summary>
public interface ITenantCacheInvalidator
{
    Task InvalidateAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>The invalidator of a store that caches nothing.</summary>
internal sealed class NoTenantCache : ITenantCacheInvalidator
{
    public static readonly NoTenantCache Instance = new();

    public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
}
