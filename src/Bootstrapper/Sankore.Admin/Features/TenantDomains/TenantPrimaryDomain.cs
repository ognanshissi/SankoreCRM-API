using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains;

/// <summary>
/// Keeps the <see cref="TenantDomain"/> row of a tenant's main origin (<c>Tenants.Fqdn</c>) in step
/// with it. Resolution reads <c>Tenants.Fqdn</c> first, so the row is not needed to open the tenant —
/// but the platform's « Domaines » list reads the rows alone, and a tenant created from the platform
/// used to have none: its main address was missing from its own list (2026-11-09). Only the start-up
/// seeder wrote one, for the default tenant.
/// </summary>
internal static class TenantPrimaryDomain
{
    /// <summary>
    /// True when <paramref name="fqdn"/> (normalised) is already a domain of ANOTHER tenant — a main
    /// origin equal to it would win the lookup and take that tenant's address.
    /// </summary>
    public static Task<bool> TakenByAnotherTenantAsync(AdminDbContext db, Guid? tenantId, string fqdn, CancellationToken ct)
        => db.TenantDomains.AnyAsync(d => d.Fqdn == fqdn && d.TenantId != tenantId, ct);

    /// <summary>
    /// Makes <paramref name="fqdn"/> the tenant's only primary row: an existing row of the tenant for it
    /// is promoted, otherwise one is added; every other primary is demoted (kept, still resolvable).
    /// Does not save.
    /// </summary>
    public static async Task SyncAsync(AdminDbContext db, Guid tenantId, string fqdn, CancellationToken ct)
    {
        var rows = await db.TenantDomains.Where(d => d.TenantId == tenantId).ToListAsync(ct);

        foreach (var other in rows.Where(d => d.IsPrimary && !string.Equals(d.Fqdn, fqdn, StringComparison.OrdinalIgnoreCase)))
            other.UnsetPrimary();

        var own = rows.FirstOrDefault(d => string.Equals(d.Fqdn, fqdn, StringComparison.OrdinalIgnoreCase));
        if (own is null)
            db.TenantDomains.Add(TenantDomain.Create(tenantId, fqdn, isPrimary: true));
        else if (!own.IsPrimary)
            own.SetPrimary();
    }
}
