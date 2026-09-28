namespace Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire on-demand job (US-M01-BE-23) — recomputes the phonetic keys of every client of one
/// tenant whose <c>PhoneticKeyPrimary</c> is still null: clients created before the keys existed,
/// or imported by a path that did not compute them. Duplicate detection blocks on those keys, so a
/// client without them is invisible to the nightly job.
/// <para>
/// Runs under the SYSTEM identity (<see cref="BackgroundJobContext.SetScope"/>): no HTTP context,
/// no end user, so the audit trail attributes the writes to SYSTEM. Reads are
/// <c>IgnoreQueryFilters()</c> + an explicit <c>TenantId</c> predicate — the ambient tenant context
/// exists, but relying on it silently would hide a cross-tenant scan.
/// </para>
/// <para>
/// Batched by <see cref="BatchSize"/> so a 100 000-client tenant never loads in one go; the change
/// tracker is cleared between batches to keep the working set flat.
/// </para>
/// </summary>
public sealed class BackfillPhoneticKeysJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Clients loaded, updated and saved per round trip.</summary>
    public const int BatchSize = 500;

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<CustomersDbContext>();
        var calculator = sp.GetRequiredService<IPhoneticKeyCalculator>();
        var logger = sp.GetRequiredService<ILogger<BackfillPhoneticKeysJob>>();

        // Clients whose names produce no key at all (blank, digits only) would be selected again
        // on every round and spin the loop forever — they are parked here instead.
        var unresolvable = new List<Guid>();
        var updated = 0;

        while (true)
        {
            var batch = await db.Clients
                .AsTracking()
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId
                         && c.PhoneticKeyPrimary == null
                         && !unresolvable.Contains(c.Id))
                .OrderBy(c => c.CreatedAt)
                .Take(BatchSize)
                .ToListAsync();

            if (batch.Count == 0)
                break;

            var changedInBatch = 0;

            foreach (var client in batch)
            {
                // A legal entity has a single name; an individual keys on last name (primary)
                // and first name (secondary), which is what ClientMatchScorer compares.
                var primary = client.Type == Domain.ClientType.Legal
                    ? calculator.Compute(client.LegalName)
                    : calculator.Compute(client.LastName);

                var secondary = client.Type == Domain.ClientType.Legal
                    ? null
                    : calculator.Compute(client.FirstName);

                if (primary is null)
                {
                    unresolvable.Add(client.Id);
                    continue;
                }

                client.SetPhoneticKeys(primary, secondary);
                changedInBatch++;
            }

            if (changedInBatch > 0)
            {
                await db.SaveChangesAsync();
                updated += changedInBatch;
            }

            db.ChangeTracker.Clear();

            // Nothing left to page through: the batch was not full.
            if (batch.Count < BatchSize)
                break;
        }

        logger.LogInformation(
            "Phonetic key backfill for tenant {TenantId}: {Updated} client(s) updated, {Skipped} without a usable name.",
            tenantId, updated, unresolvable.Count);
    }
}
