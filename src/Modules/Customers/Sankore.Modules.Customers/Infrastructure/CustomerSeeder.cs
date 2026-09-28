namespace Sankore.Modules.Customers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Brings every active tenant up to the module's baseline configuration: one
/// <see cref="CustomerSetting"/> row per declared key and one <see cref="LegalForm"/>
/// row per default OHADA legal form.
///
/// Runs on every boot and is idempotent by construction: it reads what the tenant
/// already has and inserts only what is missing, so an operator who deleted a legal
/// form or changed a threshold keeps their change across restarts — the seeder never
/// overwrites an existing row.
/// </summary>
internal static class CustomerSeeder
{
    internal static async Task SeedAsync(
        CustomersDbContext db,
        ITenantStore tenantStore,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(logger);

        var tenants = await tenantStore.GetAllActiveAsync(ct);

        foreach (var tenant in tenants)
        {
            // Guid.Empty is the SYSTEM placeholder, never a real tenant.
            if (tenant.Id == Guid.Empty)
                continue;

            var insertedSettings = await SeedSettingsAsync(db, tenant.Id, ct);
            var insertedForms = await SeedLegalFormsAsync(db, tenant.Id, ct);

            if (insertedSettings == 0 && insertedForms == 0)
                continue;

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Customers module seeded tenant {TenantId}: {SettingCount} setting(s), {LegalFormCount} legal form(s) added.",
                tenant.Id, insertedSettings, insertedForms);
        }
    }

    private static async Task<int> SeedSettingsAsync(CustomersDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit predicate: the seeder runs at startup where
        // the ambient ITenantContext is not the tenant being seeded.
        var existing = await db.CustomerSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.Key)
            .ToListAsync(ct);

        var present = new HashSet<string>(existing, StringComparer.Ordinal);
        var added = 0;

        foreach (var declared in CustomerSettingKeys.Defaults)
        {
            if (!present.Add(declared.Key))
                continue;

            db.CustomerSettings.Add(CustomerSetting.Create(
                tenantId, declared.Key, declared.Value, declared.ValueType, declared.Description));
            added++;
        }

        return added;
    }

    private static async Task<int> SeedLegalFormsAsync(CustomersDbContext db, Guid tenantId, CancellationToken ct)
    {
        var existing = await db.LegalForms
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId)
            .Select(f => f.Code)
            .ToListAsync(ct);

        var present = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var added = 0;

        // Display order follows the declared order, spaced by 10 so a tenant can slot
        // its own forms between the defaults without renumbering the list.
        for (var i = 0; i < DefaultLegalForms.All.Count; i++)
        {
            var (code, label) = DefaultLegalForms.All[i];
            if (!present.Add(code))
                continue;

            db.LegalForms.Add(LegalForm.Create(tenantId, code, label, (i + 1) * 10));
            added++;
        }

        return added;
    }
}

