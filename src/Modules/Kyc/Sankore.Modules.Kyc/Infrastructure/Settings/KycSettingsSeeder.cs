namespace Sankore.Modules.Kyc.Infrastructure.Settings;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Gives every active tenant a row for each declared KYC parameter.
///
/// Idempotent per key, not per tenant: a tenant that already has some rows receives only the keys
/// it lacks, so adding a parameter to <see cref="KycSettingKeys.Defaults"/> reaches existing
/// tenants on the next start-up without ever overwriting a value an administrator changed.
/// </summary>
internal static class KycSettingsSeeder
{
    public static async Task SeedAsync(
        KycDbContext db, ITenantStore tenantStore, TimeProvider clock, ILogger logger,
        CancellationToken ct = default)
    {
        var tenants = await tenantStore.GetAllActiveAsync(ct);
        if (tenants.Count == 0) return;

        var existing = await db.KycSettings
            .IgnoreQueryFilters()
            .Select(s => new { s.TenantId, s.Key })
            .ToListAsync(ct);

        var present = existing
            .GroupBy(x => x.TenantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var added = 0;

        foreach (var tenant in tenants)
        {
            var known = present.GetValueOrDefault(tenant.Id) ?? [];

            foreach (var definition in KycSettingKeys.Defaults)
            {
                if (known.Contains(definition.Key)) continue;

                db.KycSettings.Add(KycSetting.FromDefault(tenant.Id, definition, clock));
                added++;
            }
        }

        if (added == 0) return;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Seeded {Count} KYC setting(s) across {TenantCount} tenant(s)", added, tenants.Count);
    }
}
