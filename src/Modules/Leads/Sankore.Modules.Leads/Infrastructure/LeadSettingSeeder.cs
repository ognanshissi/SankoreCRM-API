namespace Sankore.Modules.Leads.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Brings every active tenant up to the module's baseline configuration: one
/// <see cref="LeadSetting"/> row per declared key.
///
/// Runs on every boot and is idempotent by construction: it reads what the tenant already has and
/// inserts only what is missing, so an operator who changed a retention period keeps their change
/// across restarts — the seeder never overwrites an existing row. Same contract as M01's
/// <c>CustomerSeeder</c>.
/// </summary>
internal static class LeadSettingSeeder
{
    internal static async Task SeedAsync(
        LeadsDbContext db,
        ITenantStore tenantStore,
        ILogger logger,
        CancellationToken ct = default)
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

            var added = await SeedSettingsAsync(db, tenant.Id, ct);
            if (added == 0)
                continue;

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Leads module seeded tenant {TenantId}: {SettingCount} setting(s) added.",
                tenant.Id, added);
        }
    }

    private static async Task<int> SeedSettingsAsync(LeadsDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit predicate: the seeder runs at startup where the ambient
        // ITenantContext is not the tenant being seeded.
        var existing = await db.LeadSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.Key)
            .ToListAsync(ct);

        var present = new HashSet<string>(existing, StringComparer.Ordinal);
        var added = 0;

        foreach (var declared in LeadSettingKeys.Defaults)
        {
            if (!present.Add(declared.Key))
                continue;

            db.LeadSettings.Add(LeadSetting.Create(
                tenantId, declared.Key, declared.Value, declared.ValueType, declared.Description));
            added++;
        }

        return added;
    }
}
