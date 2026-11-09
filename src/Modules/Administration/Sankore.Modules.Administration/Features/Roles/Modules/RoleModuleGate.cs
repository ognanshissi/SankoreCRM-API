using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.Modules;

/// <summary>
/// A built-in role of a paid module is not given in an institution without that module
/// (<c>docs/isolation-socle.md</c>, lot 2): creating a user, assigning or bulk-assigning a role,
/// importing users. A user who already holds it keeps it.
/// </summary>
internal static class RoleModuleGate
{
    public const string Disabled = "ROLE_MODULE_DISABLED";

    public const string Message =
        "ROLE_MODULE_DISABLED : ce rôle relève d'un module qui n'est pas activé pour votre institution.";

    /// <summary>A permission of a paid module is not granted to a role in an institution without it.</summary>
    public const string PermissionMessage =
        "PERMISSION_MODULE_DISABLED : ce droit relève d'un module qui n'est pas activé pour votre institution.";

    public static async Task<bool> RefusesAsync(ITenantModules? modules, Guid tenantId, string? roleCode, CancellationToken ct)
    {
        var scope = ModuleScopes.OfRole(roleCode);
        if (modules is null || scope.Count == 0) return false;
        return !ModuleScopes.InReach(scope, await modules.EnabledSetAsync(tenantId, ct));
    }
}
