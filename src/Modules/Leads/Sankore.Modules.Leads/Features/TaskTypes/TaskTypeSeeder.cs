namespace Sankore.Modules.Leads.Features.TaskTypes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Gives every active tenant the system task types of <see cref="CrmTaskType.System"/>.
///
/// <para>
/// Same contract as <c>LeadSettingSeeder</c>: it inserts what is missing and never updates an
/// existing row, so a label or an order an administrator changed survives every restart. A row a
/// tenant created by hand with a system code before this seeder existed is left as it is — the code
/// still behaves as a system type (<see cref="CrmTaskType.IsSystem"/>), whatever the row says.
/// </para>
/// </summary>
internal static class TaskTypeSeeder
{
    public static async Task SeedAsync(
        LeadsDbContext db,
        ITenantStore tenantStore,
        ILogger logger,
        CancellationToken ct = default)
    {
        var tenants = await tenantStore.GetAllActiveAsync(ct);

        foreach (var tenant in tenants)
        {
            // Guid.Empty is the SYSTEM placeholder, never a real tenant.
            if (tenant.Id == Guid.Empty) continue;

            var added = await SeedTenantAsync(db, tenant.Id, ct);
            if (added == 0) continue;

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Leads module seeded tenant {TenantId}: {Count} system task type(s) added.", tenant.Id, added);
        }
    }

    /// <summary>Adds the tenant's missing system types to the context; the caller saves.</summary>
    internal static async Task<int> SeedTenantAsync(LeadsDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit predicate: at startup the ambient tenant is not this one.
        var existing = await db.TaskTypeConfigs.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Select(t => t.Code)
            .ToListAsync(ct);

        var missing = Missing(existing);
        foreach (var type in missing)
            db.TaskTypeConfigs.Add(TaskTypeConfig.Create(
                tenantId, type.Code, type.Label, type.Description, isSystem: true, displayOrder: type.DisplayOrder));

        return missing.Count;
    }

    internal static IReadOnlyList<SystemTaskType> Missing(IReadOnlyCollection<string> existingCodes)
        => CrmTaskType.System.Where(t => !existingCodes.Contains(t.Code)).ToList();
}
