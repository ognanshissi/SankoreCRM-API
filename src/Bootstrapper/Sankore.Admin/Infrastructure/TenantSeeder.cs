using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure;

/// <summary>
/// Seeds a default dev tenant and its primary domain on first startup.
/// Idempotent — skips seeding if any tenant already exists.
/// </summary>
internal static class TenantSeeder
{
    private static readonly Guid DevTenantId = new("2fae736d-9c5c-456e-8d3c-5e4e9b0674ce");
    private const string DevFqdn = "http://localhost:4222";

    public static async Task SeedAsync(AdminDbContext db, CancellationToken ct = default)
    {
        if (await db.Tenants.AnyAsync(ct))
            return;

        var devTenant = Tenant.Create(
            name: "Sankore Dev",
            rootUserEmail: "admin@sankore.dev",
            applicationUrl: "http://localhost:4222",
            fqdn: DevFqdn);

        SetId(devTenant, DevTenantId);

        devTenant.ApplicationModulesList.AddRange([
            ApplicationModules.Lead,
            ApplicationModules.Customer,
            ApplicationModules.Loan
        ]);

        var primaryDomain = TenantDomain.Create(
            tenantId: DevTenantId,
            fqdn: DevFqdn,
            isPrimary: true);

        db.Tenants.Add(devTenant);
        db.TenantDomains.Add(primaryDomain);

        await db.SaveChangesAsync(ct);
    }

    // Sets the private Id property via reflection — used only here to pin the seed tenant ID.
    private static void SetId(Tenant tenant, Guid id)
    {
        var prop = typeof(Tenant).GetProperty(nameof(Tenant.Id))!;
        prop.SetValue(tenant, id);
    }
}
