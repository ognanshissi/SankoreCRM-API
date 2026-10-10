namespace Sankore.Modules.Leads.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Features.FindDuplicates;

/// <summary>
/// Fills <c>email_enc</c> and <c>email_blind_index</c> for leads captured before e-mail protection
/// shipped (BNF-B-01), reading the clear value from the transitional <c>email</c> column.
///
/// <para>
/// <b>Why this cannot be SQL in the migration.</b> Both target columns need the module's keys — one
/// to encrypt, one to HMAC — and a migration runs with no application services. So the schema
/// change is additive and non-destructive, and the DATA move happens here.
/// </para>
///
/// <para>
/// <b>Why at start-up and not as a Hangfire job.</b> Until a row's index is filled, the only thing
/// that finds it by e-mail is the transitional fallback branch in the dedup queries, and that
/// fallback is the sort of thing that gets tidied away in six months. Completing the move before
/// the host serves its first request keeps the window measured in seconds rather than in however
/// long a queue takes. It runs next to <c>SdkVersionSeeder</c> and <c>DispatchingRuleSeeder</c>,
/// which are already there for the same reason.
/// </para>
///
/// <para>
/// Idempotent and resumable by construction: it only ever selects rows whose index is still null,
/// so an interrupted boot picks up exactly where it stopped and a completed one costs a single
/// indexed count.
/// </para>
/// </summary>
internal static class LeadEmailProtectionBackfill
{
    /// <summary>
    /// Rows per transaction. Small enough that an interrupted boot loses little work, large enough
    /// that a few thousand leads are a handful of round-trips.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Ceiling on batches per boot — 250 000 rows. A guard against a loop that stops making
    /// progress (a row whose legacy value normalises to nothing would otherwise be re-selected
    /// for ever); the next boot resumes, so the ceiling delays completion rather than preventing it.
    /// </summary>
    private const int MaxBatchesPerRun = 500;

    internal static async Task RunAsync(
        LeadsDbContext db,
        ILeadEmailIndexer emailIndexer,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(emailIndexer);
        ArgumentNullException.ThrowIfNull(logger);

        var migrated = 0;
        var skipped = 0;

        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            // IgnoreQueryFilters with NO tenant predicate, which is the one place in this module
            // where that is right: the start-up scope has no ambient tenant, and the keys are
            // per-module rather than per-tenant, so every row is processed the same way. Nothing
            // here reads or compares a value across tenants.
            var pending = await db.Leads
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(l => l.EmailBlindIndex == null && l.LegacyEmailClear != null)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (pending.Count == 0)
                break;

            foreach (var lead in pending)
            {
                var index = emailIndexer.Compute(lead.LegacyEmailClear);

                if (index is null)
                {
                    // A legacy column holding only whitespace. Clearing it is what takes the row
                    // out of the pending set — otherwise it is selected again on every boot for
                    // ever, which is what MaxBatchesPerRun exists to survive.
                    lead.ClearLegacyEmail();
                    skipped++;
                    continue;
                }

                lead.BackfillEmailProtection(index);
                migrated++;
            }

            await db.SaveChangesAsync(ct);

            // The tracker holds the whole batch and nothing needs it afterwards; leaving it
            // attached would make each successive SaveChanges re-examine every earlier row.
            db.ChangeTracker.Clear();
        }

        if (migrated > 0 || skipped > 0)
        {
            logger.LogInformation(
                "Lead e-mail protection backfill: {Migrated} lead(s) encrypted and indexed, "
                + "{Skipped} blank legacy value(s) cleared.",
                migrated, skipped);
        }

        var remaining = await db.Leads
            .IgnoreQueryFilters()
            .CountAsync(l => l.EmailBlindIndex == null && l.LegacyEmailClear != null, ct);

        if (remaining > 0)
        {
            // Warning and not an exception: a partially-backfilled table is correct, just slower —
            // the dedup fallback still finds those rows. But the legacy column may NOT be dropped
            // while this number is non-zero, and this line is the only place that says so.
            logger.LogWarning(
                "Lead e-mail protection backfill stopped with {Remaining} lead(s) still unprotected "
                + "(batch ceiling reached). It resumes on the next start-up. Do not drop "
                + "leads.leads.email until this reports zero.",
                remaining);
        }
    }
}
