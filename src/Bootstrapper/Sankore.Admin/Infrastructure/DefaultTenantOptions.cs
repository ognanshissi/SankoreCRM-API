using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Infrastructure;

/// <summary>
/// The tenant <see cref="TenantSeeder"/> provisions on start-up, read from configuration so a
/// deployment does not have to POST /api/v1/tenants by hand before anything works.
///
/// <para>
/// <b><see cref="Fqdn"/> is an ORIGIN, scheme included</b> — <c>https://crm.example.com</c>, not
/// <c>crm.example.com</c> and not the API's own host. The front-end stamps every call with
/// <c>location.origin</c> (SankoreFront <c>tenant-provider.ts</c> → <c>tenant.interceptor.ts</c>),
/// and <c>GetTenantByFqdnHandler</c> matches that string against <see cref="Tenant.Fqdn"/>
/// whole. A bare host is therefore never found, and the symptom is every request answering
/// 400 "Cannot determine tenant" with nothing wrong in the logs — which is why
/// <see cref="Validate"/> refuses one at start-up instead.
/// </para>
/// </summary>
public sealed class DefaultTenantOptions
{
    public const string Section = "DefaultTenant";

    /// <summary>Public origin of the front-end, scheme included. Empty disables seeding.</summary>
    public string Fqdn { get; set; } = string.Empty;

    /// <summary>Address of the tenant's root user. Empty disables seeding.</summary>
    public string RootUserEmail { get; set; } = string.Empty;

    public string Name { get; set; } = DefaultName;

    /// <summary>
    /// Tenant id to pin, as a string rather than a <c>Guid?</c>: it arrives from the environment
    /// (<c>DefaultTenant__Id</c>), where "unset" is an empty value, and a malformed one must be
    /// reportable as such instead of failing the configuration binder.
    ///
    /// <para>
    /// Worth pinning in any deployment that keeps data. Every row of the API's database is keyed
    /// by this id; a registry rebuilt without it generates a fresh Guid, and the whole history
    /// becomes invisible to a tenant that looks identical from the outside.
    /// </para>
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Paid modules the seeded tenant receives, as comma-separated <see cref="TenantModules"/>
    /// codes (<c>DefaultTenant__ActiveModules</c>); empty grants none. Granted once, when the
    /// seeder creates the tenant, as direct activations — never re-applied to an existing tenant,
    /// so a module its administrator switched off stays off across redeploys. Both by default:
    /// the seeded tenant is the one every feature is developed and demonstrated on.
    ///
    /// <para>
    /// A string and not a list: the configuration binder APPENDS to a list that already holds
    /// defaults, so an environment naming one module would still have yielded both.
    /// </para>
    /// </summary>
    public string ActiveModules { get; set; } = string.Join(',', TenantModules.All);

    /// <summary>The configured codes, trimmed and de-duplicated; unknown codes are kept for reporting.</summary>
    public IReadOnlyList<string> ActiveModuleCodes =>
        ActiveModules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal const string DefaultName = "Sankore";

    /// <summary>True when neither the origin nor the address is configured.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Fqdn) || string.IsNullOrWhiteSpace(RootUserEmail);

    /// <summary>Shared with Sankore.Api's system-user seeder — see <see cref="TenantFqdn"/>.</summary>
    public static string NormalizeFqdn(string? value) => TenantFqdn.Normalize(value);

    /// <summary>
    /// Checks what can be checked without touching the database. Returns false with a message
    /// meant to be logged as-is: the seeder reports and gives up rather than bringing the
    /// registry down, because an operator can still provision a tenant through the API.
    /// </summary>
    public bool Validate(out string? error)
    {
        error = null;

        if (!TenantFqdn.IsOrigin(Fqdn, out var problem))
        {
            error = $"{Section}:Fqdn (DEFAULT_TENANT_FQDN) {problem}";
            return false;
        }

        if (RootUserEmail.Trim().Length > 100)
        {
            // tenants.root_user_email is varchar(100); the insert would fail with a Postgres
            // error naming a column instead of a setting.
            error = $"{Section}:RootUserEmail exceeds the 100 characters the column holds.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Id) && !Guid.TryParse(Id, out _))
        {
            error = $"{Section}:Id is \"{Id}\", which is not a GUID. Leave it empty to generate one.";
            return false;
        }

        return true;
    }

    /// <summary>The pinned id, or null when none is configured. Call after <see cref="Validate"/>.</summary>
    public Guid? ResolvedId =>
        !string.IsNullOrWhiteSpace(Id) && Guid.TryParse(Id, out var id) ? id : null;
}
