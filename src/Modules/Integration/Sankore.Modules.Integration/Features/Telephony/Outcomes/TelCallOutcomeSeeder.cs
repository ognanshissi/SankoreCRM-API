namespace Sankore.Modules.Integration.Features.Telephony.Outcomes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Brings every active tenant up to the baseline call-outcome referential (TEL-B-11).
///
/// <para>
/// Idempotent by construction and on the same contract as <c>LeadSettingSeeder</c> and M01's
/// <c>CustomerSeeder</c>: it reads what the tenant already has and inserts only what is missing.
/// An administrator who renamed "Joint" or deactivated "Mauvais numéro" keeps their change across
/// every restart — <b>the seeder never updates an existing row</b>, which is the whole difference
/// between a baseline and a reset.
/// </para>
///
/// <para>
/// <b>It is the first per-tenant seeder this module has, and that is deliberate rather than
/// overdue.</b> <c>IntegrationModule.InitializeAsync</c> says in as many words that there is
/// nothing a connection or a mapping table could be seeded with that would be true for every
/// tenant. A call outcome is the exception: the codes carry behaviour the rest of the family
/// depends on, so an empty table is a broken feature rather than an unconfigured one.
/// </para>
///
/// <para>
/// <b>Not the trap of <c>TaskTypeConfig</c></b>, which claims to be seeded and is not: this one is
/// called from <c>InitializeAsync</c> and a test asserts the baseline arrives.
/// </para>
/// </summary>
internal static class TelCallOutcomeSeeder
{
    internal static async Task SeedAsync(
        IntegrationDbContext db,
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
            if (tenant.Id == Guid.Empty) continue;

            var added = await SeedTenantAsync(db, tenant.Id, ct);

            if (added == 0) continue;

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Telephony: tenant {TenantId} seeded with {OutcomeCount} call outcome(s).",
                tenant.Id, added);
        }
    }

    /// <summary>
    /// Inserts the codes this tenant is missing. Internal so a test can assert the baseline and
    /// its idempotency without an ITenantStore.
    /// </summary>
    internal static async Task<int> SeedTenantAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters plus an explicit predicate: the seeder runs at start-up, where the
        // ambient ITenantContext is not the tenant being seeded.
        var existing = await db.TelCallOutcomes
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId)
            .Select(o => o.Code)
            .ToListAsync(ct);

        var present = new HashSet<string>(existing, StringComparer.Ordinal);
        var added = 0;

        foreach (var seed in TelCallOutcomeDefaults.All)
        {
            // TelCallOutcome.Create upper-cases the code, so the comparison is against the stored
            // form rather than the literal — a baseline entry written in lower case would
            // otherwise be re-inserted on every boot and collide on the key.
            if (present.Contains(seed.Code.ToUpperInvariant())) continue;

            db.TelCallOutcomes.Add(TelCallOutcome.Create(
                tenantId,
                seed.Code,
                seed.Label,
                seed.CountsAsContact,
                seed.RequiresCallbackDate,
                seed.Order,
                isSystem: true));

            added++;
        }

        return added;
    }
}
