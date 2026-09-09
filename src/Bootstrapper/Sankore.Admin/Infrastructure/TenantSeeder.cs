using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure;

/// <summary>
/// Seeds a default dev tenant on first startup. Idempotent — skips seeding
/// if any tenant already exists in the database.
/// </summary>
internal static class TenantSeeder
{
    private static readonly Guid DevTenantId = new("2fae736d-9c5c-456e-8d3c-5e4e9b0674ce");

    public static async Task SeedAsync(AdminDbContext db, CancellationToken ct = default)
    {
        if (await db.Tenants.AnyAsync(ct))
            return;

        var devTenant = Tenant.Create(
            name: "Sankore Dev",
            rootUserEmail: "admin@sankore.dev",
            fqdn: "sankore.dev");

        // Fix the ID to a stable well-known value so other services can reference it.
        SetId(devTenant, DevTenantId);

        devTenant.ApplicationModulesList.AddRange([
            ApplicationModules.Lead,
            ApplicationModules.Customer,
            ApplicationModules.Loan
        ]);

        db.Tenants.Add(devTenant);
        await db.SaveChangesAsync(ct);
    }

    // Sets the private Id field via reflection — used only here to pin the seed tenant ID.
    private static void SetId(Tenant tenant, Guid id)
    {
        var prop = typeof(Tenant).GetProperty(nameof(Tenant.Id))!;
        prop.SetValue(tenant, id);
    }
}