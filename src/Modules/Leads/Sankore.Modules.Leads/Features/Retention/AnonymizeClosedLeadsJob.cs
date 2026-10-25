namespace Sankore.Modules.Leads.Features.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Destroys the personal data of leads that have been closed for longer than the tenant's
/// retention period (BNF-B-19).
///
/// <para>
/// <b>Five tables, one transaction per lead batch</b>: the lead itself, its household members,
/// its qualification responses, its consents and its FATCA declarations. All five or none — a
/// lead whose name is gone and whose spouse's name is not has not been anonymised, it has been
/// half-anonymised, and nothing would ever come back to finish the job because the lead no longer
/// matches the selection.
/// </para>
///
/// <para>
/// <b>An ExistingCustomer lead touches ONLY lead-owned data.</b> Its principal's identity lives on
/// the M01 client record, which has its own retention rule, its own job and its own regulator-facing
/// evidence (BNF-B-23). This job never reaches across — a lead's retention period expiring is not
/// a reason to destroy a client the institution still serves.
/// </para>
///
/// <para>
/// <b>The row survives.</b> What it says about the funnel — source, campaign, score, status,
/// stage, agency, dates — is the statistics an institution measures itself on, and destroying it
/// would make a retention rule a reporting outage. See <c>Lead.Anonymize</c> for the full list of
/// what goes and what stays.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type, like every other job here.
public sealed class AnonymizeClosedLeadsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<AnonymizeClosedLeadsJob> logger)
{
    /// <summary>
    /// Leads per transaction. Deliberately small: each one drags four child collections with it,
    /// and an interrupted run should lose one batch of work rather than an hour of it.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// A ceiling per run, so a first run against years of backlog cannot hold a connection for
    /// the whole night. What is left is picked up by the next run — the selection is
    /// self-advancing, because an anonymised lead no longer matches it.
    /// </summary>
    private const int MaxBatchesPerRun = 100;

    /// <summary>
    /// Leads one run can anonymise at most. Public because the retention PREVIEW reports it: a
    /// backlog larger than this is worked through over several nights, and an administrator
    /// watching the due count fall in steps needs to know that is the ceiling and not a fault.
    /// </summary>
    public const int MaxLeadsPerRun = BatchSize * MaxBatchesPerRun;

    /// <summary>
    /// Runs the anonymisation for one tenant.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundJobContext.SetScope</c> is established BEFORE the DI scope is created, because
    /// <c>ITenantContext</c> and <c>ICurrentUser</c> are built from it and a Hangfire job has no
    /// HTTP context. Getting that order wrong is the documented way this module's jobs fail.
    /// </remarks>
    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var settings = sp.GetRequiredService<ILeadSettings>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var months = await settings.GetIntAsync(
            tenantId, LeadSettingKeys.LeadRetentionMonths, ct);

        // Zero or negative would anonymise a lead the moment it closes. Treated as "not
        // configured" rather than honoured: a tenant asking for immediate destruction is asking
        // for something a settings typo produces by accident, and the cost of the mistake is
        // irreversible.
        if (months <= 0)
        {
            logger.LogWarning(
                "Lead anonymisation skipped for tenant {TenantId}: the retention period is {Months} "
                + "months, which would destroy a lead as soon as it closes",
                tenantId, months);
            return;
        }

        var cutoff = RetentionSelection.CutoffFor(clock.GetUtcNow(), months);
        var total = 0;

        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            // A FRESH scope, and therefore a fresh DbContext, per batch — disposed at the end of
            // each iteration. One context for the whole run would keep every lead and every child
            // of every previous batch in its change tracker, so a run over a backlog would end up
            // holding the table in memory and re-validating it on each save.
            using var batchScope = scopeFactory.CreateScope();
            var db = batchScope.ServiceProvider.GetRequiredService<LeadsDbContext>();

            var anonymized = await AnonymizeBatchAsync(db, tenantId, cutoff, clock, ct);
            total += anonymized;

            if (anonymized < BatchSize)
                break;
        }

        if (total > 0)
        {
            logger.LogInformation(
                "Anonymised {Count} leads closed before {Cutoff:yyyy-MM-dd} for tenant {TenantId}",
                total, cutoff, tenantId);
        }
    }

    private async Task<int> AnonymizeBatchAsync(
        LeadsDbContext db, Guid tenantId, DateTimeOffset cutoff, TimeProvider clock,
        CancellationToken ct)
    {
        // IgnoreQueryFilters plus an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        // The predicate itself lives in RetentionSelection, shared with the preview endpoint —
        // a preview that selected different rows would have an administrator approve one number
        // and the run destroy another.
        var leads = await db.Leads
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .Where(RetentionSelection.Due(tenantId, cutoff))
            .OrderBy(l => l.UpdatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (leads.Count == 0)
            return 0;

        await LeadErasure.AnonymizeAsync(db, tenantId, leads, clock.GetUtcNow(), ct);

        // ONE save for the whole batch: every table or none. A lead whose name is gone and
        // whose spouse's name is not would never be revisited, because it no longer matches the
        // selection above.
        await db.SaveChangesAsync(ct);

        return leads.Count;
    }
}
