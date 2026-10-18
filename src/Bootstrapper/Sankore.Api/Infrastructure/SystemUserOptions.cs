using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// The system user <see cref="SystemUserSeeder"/> provisions at start-up — the account that
/// `POST /api/v1/users/create-root` creates by hand, done automatically instead so a fresh
/// deployment has somebody who can log in.
///
/// <para>
/// <see cref="TenantFqdn"/> must be the SAME origin Sankore.Admin seeded as the tenant's FQDN
/// (<c>DEFAULT_TENANT_FQDN</c> feeds both), because that is how this host finds the tenant it is
/// provisioning for.
/// </para>
/// </summary>
public sealed class SystemUserOptions
{
    public const string Section = "SystemUser";

    /// <summary>Origin of the tenant to provision, scheme included.</summary>
    public string TenantFqdn { get; set; } = string.Empty;

    /// <summary>
    /// Empty falls back to the tenant's own <c>RootUserEmail</c> from the registry, so the common
    /// case needs one variable rather than the same address written twice.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Empty disables the whole seeder. There is no default, deliberately.</summary>
    public string Password { get; set; } = string.Empty;

    public string FirstName { get; set; } = "System";

    public string LastName { get; set; } = "Sankore";

    /// <summary>True when no password is configured — the seeder then does nothing at all.</summary>
    public bool IsDisabled => string.IsNullOrWhiteSpace(Password);
}
