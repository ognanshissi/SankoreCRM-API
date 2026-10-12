namespace Sankore.Modules.Integration.Features.Insurance.Premiums;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-08: materialises each live policy's premium schedule, then queues the debits that have
/// fallen due.
///
/// <para>
/// <b>Two passes in one job, and they belong together.</b> Generating a schedule nobody collects
/// is a table of dates; collecting from a schedule that does not exist collects nothing. Running
/// them as two recurring jobs would also let them drift apart in time, so a policy synchronised
/// at 03:30 would wait a day for its first instalment.
/// </para>
///
/// <para>
/// <b>Idempotence is the unique index</b>
/// <c>ux_ins_premium_instalment_due (tenant_id, policy_id, due_date)</c>, and the generator
/// computes a due date from the policy's own effective date so a second run produces the same
/// dates. The read below is what turns a re-run into a no-op rather than a unique violation —
/// EF InMemory honours no unique index, so that read is the only half the test suite can prove
/// and the index is what makes the rule true under concurrency.
/// </para>
///
/// <para>
/// <b>The retry policy is read from the PRODUCT at decision time</b>, never copied onto the
/// instalment: ASS-08 asks for « une politique paramétrable par produit », and an administrator
/// raising the limit is doing it for the instalments that are already failing.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class CollectPremiumsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CollectPremiumsJob> logger)
{
    /// <summary>
    /// How far ahead the schedule is materialised. A year: far enough that a policy's whole cycle
    /// is visible to a statement and a customer, short enough that a periodicity corrected at the
    /// insurer does not leave a decade of wrong dates to clean up.
    /// </summary>
    internal const int HorizonMonths = 12;

    /// <summary>Policies per page, and therefore per SQL round trip.</summary>
    private const int PolicyPageSize = 200;

    /// <summary>Instalments debited per run. A ceiling, so one night cannot hold a connection.</summary>
    private const int MaxDebitsPerRun = 500;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var integration = sp.GetRequiredService<IIntegrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var generated = await GenerateSchedulesAsync(db, tenantId, today, ct);
        var queued = await CollectDueAsync(db, integration, clock, tenantId, today, ct);

        if (generated > 0 || queued > 0)
        {
            logger.LogInformation(
                "Premiums for tenant {TenantId}: {Generated} instalment(s) scheduled, "
                + "{Queued} debit(s) queued",
                tenantId, generated, queued);
        }
    }

    // ── pass one: the schedule ──────────────────────────────────────────────

    private async Task<int> GenerateSchedulesAsync(
        IntegrationDbContext db, Guid tenantId, DateOnly today, CancellationToken ct)
    {
        var horizon = today.AddMonths(HorizonMonths);
        var created = 0;
        var page = 0;

        while (true)
        {
            // Live policies only. A cancelled or expired contract owes nothing further, and
            // scheduling for one would debit a premium for cover the customer no longer has.
            var policies = await db.Policies
                .IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId
                         && (p.Status == PolicyStatus.Issued || p.Status == PolicyStatus.Suspended))
                .OrderBy(p => p.Id)
                .Skip(page * PolicyPageSize)
                .Take(PolicyPageSize)
                .Select(p => new
                {
                    p.Id,
                    p.EffectiveDate,
                    p.ExpiryDate,
                    p.PremiumAmount,
                    p.Currency,
                    p.Periodicity,
                })
                .ToListAsync(ct);

            if (policies.Count == 0) break;

            var policyIds = policies.Select(p => p.Id).ToList();

            // The due dates already materialised for this page, in one query. Read rather than
            // relied upon from the index: a unique violation would abort the whole night, and the
            // generator's job is to be re-runnable.
            var existing = await db.PremiumInstalments
                .IgnoreQueryFilters()
                .Where(i => i.TenantId == tenantId && policyIds.Contains(i.PolicyId))
                .Select(i => new { i.PolicyId, i.DueDate, i.SequenceNo })
                .ToListAsync(ct);

            var byPolicy = existing
                .GroupBy(i => i.PolicyId)
                .ToDictionary(
                    g => g.Key,
                    g => (Dates: g.Select(i => i.DueDate).ToHashSet(),
                          MaxSequence: g.Max(i => i.SequenceNo)));

            foreach (var policy in policies)
            {
                var wanted = PremiumSchedule.DueDates(
                    policy.EffectiveDate, horizon, policy.Periodicity, policy.ExpiryDate);

                byPolicy.TryGetValue(policy.Id, out var already);
                var seen = already.Dates ?? [];
                var sequence = already.MaxSequence;

                foreach (var due in wanted)
                {
                    if (seen.Contains(due)) continue;

                    // The sequence is 1-based and printed on a statement; it is not the key, so a
                    // gap left by a waived instalment is cosmetic rather than a correctness
                    // problem. Continuing from the highest already stored keeps it monotonic
                    // across runs.
                    sequence++;

                    db.PremiumInstalments.Add(PremiumInstalment.Create(
                        tenantId: tenantId,
                        policyId: policy.Id,
                        sequenceNo: sequence,
                        dueDate: due,
                        amount: policy.PremiumAmount,
                        currency: policy.Currency,
                        clock: TimeProvider.System));

                    created++;
                }
            }

            await db.SaveChangesAsync(ct);

            if (policies.Count < PolicyPageSize) break;
            page++;
        }

        return created;
    }

    // ── pass two: the debits ────────────────────────────────────────────────

    private async Task<int> CollectDueAsync(
        IntegrationDbContext db,
        IIntegrationModule integration,
        TimeProvider clock,
        Guid tenantId,
        DateOnly today,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Due, and either never attempted or past its next attempt. The status pair is
        // IsDebitable's own definition, expressed in SQL because the index serves it — a
        // materialised filter over the aggregate's property would read the whole table.
        //
        // AsTracking is MANDATORY here, and its absence is silent: the context defaults to
        // NoTracking, so BeginDebit and CancelBecausePolicyEnded below would mutate detached
        // entities and SaveChangesAsync would write nothing — the debit would be sent to the core
        // banking system on every run, for ever, with no row ever moving out of Scheduled.
        var due = await db.PremiumInstalments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(i => i.TenantId == tenantId
                     && i.DueDate <= today
                     && (i.Status == InstalmentStatus.Scheduled
                      || (i.Status == InstalmentStatus.Failed
                          && i.NextAttemptAt != null
                          && i.NextAttemptAt <= now)))
            .OrderBy(i => i.DueDate)
            .Take(MaxDebitsPerRun)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        // Everything the decision needs, in two queries rather than per instalment: the account
        // to debit lives on the policy's customer reference, and the retry policy on the product.
        var policyIds = due.Select(i => i.PolicyId).Distinct().ToList();

        var policies = await db.Policies
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && policyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CrmCustomerId, p.ProductId, p.PolicyNumber })
            .ToListAsync(ct);

        var policyById = policies.ToDictionary(p => p.Id);

        // The product's retry policy is deliberately NOT read here: this job only queues a
        // debit, and the budget is spent by the OUTCOME. PremiumOutcomeConsumers compares the
        // limit at the moment a debit fails, which is what makes « une politique paramétrable par
        // produit » apply to the instalments already failing rather than to the ones that start
        // failing after an administrator changes it.
        var queued = 0;

        foreach (var instalment in due)
        {
            if (!policyById.TryGetValue(instalment.PolicyId, out var policy))
            {
                // The policy vanished under its own schedule. Cancelled rather than retried for
                // ever: there is no cover to collect a premium for.
                instalment.CancelBecausePolicyEnded(clock);
                continue;
            }

            var account = await ResolveAccountAsync(db, tenantId, policy.CrmCustomerId, ct);

            if (account is null)
            {
                // No account known for this customer at the core banking system. NOT a failure of
                // the instalment — nothing was attempted — so it stays Scheduled and tomorrow's
                // run tries again once the reference exists. Logged, because a portfolio where
                // this never resolves is a configuration fault.
                logger.LogWarning(
                    "Premium instalment {InstalmentId} could not be debited: no core-banking "
                    + "account reference for customer {CustomerId}",
                    instalment.Id, policy.CrmCustomerId);
                continue;
            }

            try
            {
                var commandId = await integration.CoreBanking.RequestInstalmentDebitAsync(
                    new InstalmentDebitRequest(
                        InstalmentId: instalment.Id,
                        AccountRef: account,
                        Amount: instalment.Amount,
                        Currency: instalment.Currency,
                        Attempt: instalment.Attempts + 1,
                        Label: $"Prime {policy.PolicyNumber} éch. {instalment.SequenceNo}"),
                    ct);

                instalment.BeginDebit(commandId.Value, clock);
                queued++;
            }
            catch (DomainException ex)
            {
                // The tenant has no active core-banking connection. The instalment is untouched,
                // so tomorrow's run tries again — and an administrator's to fix, not a retry's.
                logger.LogWarning(
                    "Premium instalment {InstalmentId} not queued: {Reason}",
                    instalment.Id, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);

        return queued;
    }

    /// <summary>
    /// The account to debit: the customer's core-banking reference. Through the reference table,
    /// which is the only route this module has to an external identifier.
    /// </summary>
    private static async Task<string?> ResolveAccountAsync(
        IntegrationDbContext db, Guid tenantId, Guid crmCustomerId, CancellationToken ct)
        => await db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                     && r.CrmId == crmCustomerId
                     && r.EntityType == IntegrationEntityTypes.Account)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.ExternalId)
            .FirstOrDefaultAsync(ct);
}
