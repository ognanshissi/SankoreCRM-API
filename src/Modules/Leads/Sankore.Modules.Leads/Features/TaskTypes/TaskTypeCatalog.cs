namespace Sankore.Modules.Leads.Features.TaskTypes;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The current tenant's task types, as every task slice reads them: which code a person may give a
/// task, and the label a code is shown with.
///
/// <para>
/// A system code is always usable, row or no row: the platform creates those tasks itself and a
/// system type cannot be deactivated. Its label falls back to the seeded one, so a tenant created
/// after the last boot — not seeded yet — still reads « Premier contact » rather than a code.
/// </para>
/// </summary>
internal sealed class TaskTypeCatalog(LeadsDbContext db, ITenantContext tenant)
{
    public const string UnknownError = "TASK_TYPE_UNKNOWN";
    public const string InactiveError = "TASK_TYPE_INACTIVE";

    /// <summary>
    /// Whether a person may give a task, or a generation rule, this type now. Unknown and
    /// deactivated are told apart: the screen can then say which one.
    /// </summary>
    public async Task<Result> CheckUsableAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return Result.Fail(UnknownError);
        if (CrmTaskType.IsSystem(code)) return Result.Ok();

        var type = await db.TaskTypeConfigs.AsNoTracking()
            .Where(t => t.Code == code)
            .Select(t => new { t.IsActive })
            .FirstOrDefaultAsync(ct);

        return type is null ? Result.Fail(UnknownError)
            : type.IsActive ? Result.Ok()
            : Result.Fail(InactiveError);
    }

    /// <summary>Code → label for every type of the tenant, deactivated ones included.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LabelsAsync(CancellationToken ct)
    {
        var labels = await db.TaskTypeConfigs.AsNoTracking()
            .Select(t => new { t.Code, t.Label })
            .ToDictionaryAsync(t => t.Code, t => t.Label, StringComparer.Ordinal, ct);

        foreach (var system in CrmTaskType.System)
            labels.TryAdd(system.Code, system.Label);

        return labels;
    }

    /// <summary>
    /// Adds the system types the current tenant is missing. The boot seeder covers the tenants
    /// that exist when the host starts; this covers one created since, the first time its types
    /// are listed.
    /// </summary>
    public async Task EnsureSystemTypesAsync(CancellationToken ct)
    {
        if (tenant.CurrentTenantId == Guid.Empty) return;
        if (await TaskTypeSeeder.SeedTenantAsync(db, tenant.CurrentTenantId, ct) == 0) return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first readings at once: the other one inserted the same rows (unique on tenant +
            // code). Its rows are as good as ours.
            db.ChangeTracker.Clear();
        }
    }
}
