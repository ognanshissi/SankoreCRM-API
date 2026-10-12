namespace Sankore.Modules.Integration.Features.Insurance.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// ASS-12: stamps the retention date on the insurance evidence, then destroys what has outlived
/// it.
///
/// <para>
/// <b>Two passes, and the first is the one that makes the second possible.</b>
/// <c>ConsentProof.RetainUntil</c> and <c>MedicalQuestionnaire.RetainUntil</c> were designed
/// nullable, with <c>null</c> meaning <i>no period has been decided</i> and explicitly not
/// "sweepable now". Nothing wrote them, so the purge had nothing it was permitted to touch. Pass
/// one applies the configured period to the rows that carry no date; pass two sweeps the dates
/// that have passed.
/// </para>
///
/// <para>
/// <b>Pass one runs even when the purge is off</b>, deliberately. Recording when a row becomes
/// purgeable destroys nothing, and a deployment that never enables the purge still carries honest
/// retention dates for a controller to read. Pass two is gated on
/// <see cref="InsuranceRetentionOptions.PurgeEnabled"/> because it destroys evidence and the
/// destruction cannot be undone — that is an operator's explicit decision per environment, not
/// something a deployment acquires by upgrading.
/// </para>
///
/// <para>
/// <b>A consent proof is blanked and a medical questionnaire is deleted</b>, and the asymmetry is
/// argued where the two aggregates live: the proof's skeleton is a compliance record with no
/// personal content, while the questionnaire's only non-answer column —
/// <c>RequiresMedicalUnderwriting</c> — IS health data about a named person.
/// </para>
///
/// <para>
/// <b>Claim documents are deliberately NOT swept.</b> <c>ins_claim_document</c> is named by
/// ASS-12 and carries no retention column, and a purge of one would have to destroy the encrypted
/// OBJECT as well as the row — a store this module does not yet have for claim pieces. Deleting
/// the row alone would orphan the bytes for ever, which is worse than keeping both, so the gap is
/// named here rather than half-closed.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class PurgeInsuranceEvidenceJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PurgeInsuranceEvidenceJob> logger)
{
    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var options = sp.GetRequiredService<IOptions<InsuranceRetentionOptions>>().Value;

        var stamped = await StampAsync(db, clock, options, tenantId, ct);

        if (!options.PurgeEnabled)
        {
            if (stamped > 0)
            {
                logger.LogInformation(
                    "Insurance retention for tenant {TenantId}: {Stamped} row(s) dated at "
                    + "{Years} years. The purge is DISABLED, so nothing was destroyed",
                    tenantId, stamped, options.ResolvedYears);
            }

            return;
        }

        var (proofs, questionnaires) = await PurgeAsync(db, clock, options, tenantId, ct);

        if (stamped > 0 || proofs > 0 || questionnaires > 0)
        {
            logger.LogInformation(
                "Insurance retention for tenant {TenantId}: {Stamped} row(s) dated, "
                + "{Proofs} consent proof(s) blanked, {Questionnaires} medical "
                + "questionnaire(s) deleted",
                tenantId, stamped, proofs, questionnaires);
        }
    }

    // ── pass one: the dates ─────────────────────────────────────────────────

    /// <summary>
    /// Applies the configured period to the rows that carry no retention date, counted from when
    /// each was CAPTURED — not from tonight. Counting from tonight would restart a decade for
    /// evidence collected years ago, which is a retention rule that never expires.
    /// </summary>
    private static async Task<int> StampAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        InsuranceRetentionOptions options,
        Guid tenantId,
        CancellationToken ct)
    {
        var years = options.ResolvedYears;
        var batch = options.ResolvedBatchSize;
        var stamped = 0;

        // IgnoreQueryFilters with an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        var proofs = await db.ConsentProofs
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId && c.RetainUntil == null)
            .OrderBy(c => c.CapturedAt)
            .Take(batch)
            .ToListAsync(ct);

        foreach (var proof in proofs)
        {
            if (proof.DecideRetention(proof.CapturedAt.AddYears(years))) stamped++;
        }

        var questionnaires = await db.MedicalQuestionnaires
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(q => q.TenantId == tenantId && q.RetainUntil == null)
            .OrderBy(q => q.CompletedAt)
            .Take(batch)
            .ToListAsync(ct);

        foreach (var questionnaire in questionnaires)
        {
            if (questionnaire.DecideRetention(questionnaire.CompletedAt.AddYears(years))) stamped++;
        }

        if (stamped > 0) await db.SaveChangesAsync(ct);

        return stamped;
    }

    // ── pass two: the destruction ───────────────────────────────────────────

    private async Task<(int Proofs, int Questionnaires)> PurgeAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        InsuranceRetentionOptions options,
        Guid tenantId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var batch = options.ResolvedBatchSize;

        // A decided date, in the past, and not already purged. The null check is in the SQL and
        // not only in the aggregate: a predicate that relied on the aggregate's refusal would
        // load every undated row on every run just to be told no.
        var expiredProofs = await db.ConsentProofs
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId
                     && c.RetainUntil != null
                     && c.RetainUntil <= now
                     && c.PurgedAt == null)
            .OrderBy(c => c.RetainUntil)
            .Take(batch)
            .ToListAsync(ct);

        var proofs = 0;

        foreach (var proof in expiredProofs)
        {
            // The aggregate re-checks the date. Belt and braces on purpose: this is the one job
            // in the module whose mistake cannot be corrected by running it again.
            if (proof.PurgeEvidence(clock)) proofs++;
        }

        var expiredQuestionnaires = await db.MedicalQuestionnaires
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(q => q.TenantId == tenantId
                     && q.RetainUntil != null
                     && q.RetainUntil <= now)
            .OrderBy(q => q.RetainUntil)
            .Take(batch)
            .ToListAsync(ct);

        // Loaded and removed rather than ExecuteDeleteAsync, which EF InMemory does not support —
        // so the one path in this module that destroys data would otherwise be untested.
        var purgeable = expiredQuestionnaires.Where(q => q.MayBePurgedAt(now)).ToList();

        db.MedicalQuestionnaires.RemoveRange(purgeable);

        if (proofs > 0 || purgeable.Count > 0) await db.SaveChangesAsync(ct);

        if (purgeable.Count > 0)
        {
            // Logged per run and not per row: the row is gone, so this log line and the proof's
            // own PurgedAt are the only trace that it existed. Said plainly rather than left for
            // somebody to discover.
            logger.LogWarning(
                "Deleted {Count} medical questionnaire(s) past their retention for tenant "
                + "{TenantId}. The rows are gone and this line is their only trace",
                purgeable.Count, tenantId);
        }

        return (proofs, purgeable.Count);
    }
}
