namespace Sankore.Shared.Kernel;

/// <summary>
/// Which paid modules (<see cref="TenantModules"/>) a tenant has in force. Read from the cached
/// <see cref="TenantInfo"/>, so asking costs no network call in the common case.
///
/// <para>
/// Every answer fails OPEN: an unknown tenant, a registry that cannot be reached, a record cached
/// before modules existed — all read as « every module ». A module is a commercial boundary; an
/// outage must not take away from a tenant what it pays for. (Compliance switches fail the other
/// way, and that is why they are not modules.)
/// </para>
/// </summary>
public interface ITenantModules
{
    /// <summary>For the current tenant (<see cref="ITenantContext"/>).</summary>
    Task<bool> IsEnabledAsync(string module, CancellationToken ct = default);

    Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct = default);

    /// <summary>The modules in force, or <see cref="TenantModules.All"/> when unknown.</summary>
    Task<IReadOnlyList<string>> ActiveAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Whether the module's data may be CONSULTED: in force, or deactivated by the tenant. A
    /// deactivated module keeps its screens read-only (product owner, 2026-10-23); a tenant that
    /// never had it reads nothing.
    /// </summary>
    Task<bool> IsReadableAsync(string module, CancellationToken ct = default);

    Task<bool> IsReadableAsync(Guid tenantId, string module, CancellationToken ct = default);

    /// <summary>The modules deactivated and not active again: readable, not writable.</summary>
    Task<IReadOnlyList<string>> ReadOnlyAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// A fixed answer, for tests and tooling with no registry to ask. <see cref="All"/> is what a
/// tenant that existed before modules reads.
/// </summary>
public sealed class FixedTenantModules(params string[] active) : ITenantModules
{
    public static FixedTenantModules All { get; } = new([.. TenantModules.All]);

    public static FixedTenantModules None { get; } = new();

    /// <summary>Deactivated modules, readable only.</summary>
    public IReadOnlyList<string> ReadOnly { get; init; } = [];

    public Task<bool> IsEnabledAsync(string module, CancellationToken ct = default)
        => Task.FromResult(active.Contains(module));

    public Task<bool> IsEnabledAsync(Guid tenantId, string module, CancellationToken ct = default)
        => IsEnabledAsync(module, ct);

    public Task<IReadOnlyList<string>> ActiveAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(active);

    public Task<bool> IsReadableAsync(string module, CancellationToken ct = default)
        => Task.FromResult(active.Contains(module) || ReadOnly.Contains(module));

    public Task<bool> IsReadableAsync(Guid tenantId, string module, CancellationToken ct = default)
        => IsReadableAsync(module, ct);

    public Task<IReadOnlyList<string>> ReadOnlyAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult(ReadOnly);
}

public static class TenantModulesExtensions
{
    /// <summary>
    /// Whether the current tenant may create a product, a template… in <paramref name="category"/>.
    /// Only creation is gated: an existing product of a module switched off stays usable.
    /// </summary>
    public static async Task<bool> AllowsCategoryAsync(this ITenantModules modules, ProductCategory category,
        CancellationToken ct = default)
        => ProductCategories.ModuleOf(category) is not { } module || await modules.IsEnabledAsync(module, ct);
}

/// <summary>Error codes of the module gate. Relayed to the front-end as they are: never rename one.</summary>
public static class TenantModuleErrors
{
    /// <summary>The route belongs to a module the tenant has not switched on.</summary>
    public const string Disabled = "TENANT_MODULE_DISABLED";

    /// <summary>A product (or template) in a category whose module the tenant has not switched on.</summary>
    public const string CategoryDisabled = "PRODUCT_CATEGORY_MODULE_DISABLED";
}
