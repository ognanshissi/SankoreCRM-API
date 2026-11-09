namespace Sankore.Shared.Kernel;

/// <summary>
/// Which paid module (<see cref="TenantModules"/>) a permission or a built-in role belongs to
/// (<c>docs/isolation-socle.md</c>, lot 2, owner's decision of 2026-11-09). Built-in roles and the
/// permission catalogue are platform-wide rows, shared by every tenant, so « not created for a
/// socle tenant » means: out of its role list, its permission catalogue, its role editor, its
/// current-user profile, and refused when assigned. A user who already holds such a role keeps it.
///
/// <para>
/// An empty answer is the socle. Several modules mean « any of them » — the integration surfaces
/// (mappings, commands, reconciliation) serve the core banking and the insurer alike. Integration
/// connections, webhooks and holdings stay in the socle: telephony, mail and Standard products use
/// them.
/// </para>
/// </summary>
public static class ModuleScopes
{
    private static readonly string[] Socle = [];
    private static readonly string[] Banking = [TenantModules.BankingMicrofinance];
    private static readonly string[] Insurance = [TenantModules.Insurance];
    private static readonly string[] Either = [TenantModules.BankingMicrofinance, TenantModules.Insurance];

    private static readonly Dictionary<string, string[]> PermissionModules = new(StringComparer.OrdinalIgnoreCase)
    {
        [Permissions.CanCreateLoan.Code] = Banking,
        [Permissions.CanManageCustomerGroups.Code] = Banking,
        [Permissions.CanViewLiveBalance.Code] = Banking,

        [Permissions.CanManageDistributionCredentials.Code] = Insurance,
        [Permissions.CanReadLeadHealth.Code] = Insurance,
        [Permissions.CanManageDesignations.Code] = Insurance,

        [Permissions.CanManageIntegrationMapping.Code] = Either,
        [Permissions.CanViewIntegrationCommand.Code] = Either,
        [Permissions.CanReplayIntegrationCommand.Code] = Either,
        [Permissions.CanViewIntegrationReconciliation.Code] = Either,
        [Permissions.CanResolveIntegrationReconciliation.Code] = Either,
    };

    private static readonly Dictionary<string, string[]> RoleModules = new(StringComparer.OrdinalIgnoreCase)
    {
        [Roles.CreditAnalyst.Code] = Banking,
        [Roles.CreditManager.Code] = Banking,
        [Roles.Cashier.Code] = Banking,
        [Roles.AccountManager.Code] = Banking,
    };

    /// <summary>The modules a permission needs (any of them); empty for the socle.</summary>
    public static IReadOnlyList<string> OfPermission(string code)
        => PermissionModules.TryGetValue(code, out var modules) ? modules
         // Every insurance permission is named « Ins.* »: one written later is covered.
         : code.StartsWith("Ins.", StringComparison.Ordinal) ? Insurance
         : Socle;

    /// <summary>The modules a built-in role needs (any of them); empty for the socle and for a role an institution created.</summary>
    public static IReadOnlyList<string> OfRole(string? code)
        => code is not null && RoleModules.TryGetValue(code, out var modules) ? modules : Socle;

    /// <summary>Whether something scoped to <paramref name="anyOf"/> is in reach of a tenant holding <paramref name="modules"/>.</summary>
    public static bool InReach(IReadOnlyList<string> anyOf, IReadOnlySet<string> modules)
        => anyOf.Count == 0 || anyOf.Any(modules.Contains);
}

public static class ModuleScopesExtensions
{
    /// <summary>
    /// The modules whose screens the tenant still sees: active ones, and deactivated ones read-only.
    /// What lists and profiles are filtered on — a module switched off keeps its consultation.
    /// </summary>
    public static async Task<IReadOnlySet<string>> ReadableSetAsync(this ITenantModules modules, Guid tenantId, CancellationToken ct = default)
        => new HashSet<string>((await modules.ActiveAsync(tenantId, ct)).Concat(await modules.ReadOnlyAsync(tenantId, ct)));

    /// <summary>The active modules only — what an assignment or a grant needs.</summary>
    public static async Task<IReadOnlySet<string>> EnabledSetAsync(this ITenantModules modules, Guid tenantId, CancellationToken ct = default)
        => new HashSet<string>(await modules.ActiveAsync(tenantId, ct));
}
