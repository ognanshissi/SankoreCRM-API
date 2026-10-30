namespace Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// What a « Mode test » access token carries beyond an ordinary one (sandbox lot 2). Its
/// <c>tenant_id</c> names the TEST TWIN — every module reads and writes the twin's data through the
/// usual claim, unchanged — and this claim names the live tenant the user's account belongs to.
/// </summary>
public static class TestModeClaims
{
    /// <summary>The live tenant; present only on a test-mode token.</summary>
    public const string TeamTenantId = "team_tenant_id";

    /// <summary>
    /// The tenant the account — and so its sessions, refresh tokens, roles — belongs to, read off
    /// the token: <see cref="TeamTenantId"/> in test mode, else <c>tenant_id</c>. For the checks
    /// that run before any <c>ITenantContext</c> exists: the session check read <c>tenant_id</c>,
    /// looked for the session in the twin, and failed every test-mode request with a 401.
    /// </summary>
    public static Guid? AccountTenantOf(System.Security.Claims.ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(TeamTenantId)?.Value ?? principal?.FindFirst("tenant_id")?.Value, out var tenant)
            ? tenant
            : null;
}
