namespace Sankore.Modules.Integration.Features.Insurance.Statements;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// ASS-10: one month's <i>bordereau</i> per insurer, and the commission it earns.
///
/// <para>
/// <b>Every line is a snapshot, and the generation is what takes it.</b> The policy number, the
/// insurer's product code, the CBS reference and the commission RATE are copied onto the line as
/// they stand tonight. A bordereau is a justification document: one that re-rendered from today's
/// catalogue would change a figure somebody has already signed the moment a rate was edited or a
/// policy cancelled. That is also why the header totals are columns and not a view.
/// </para>
///
/// <para>
/// <b>A regeneration rebuilds the DRAFT and refuses a finalised month.</b> The lines are deleted
/// and written again — which is why <c>InsurerStatementLine</c> carries no concurrency token —
/// and <c>ApplyTotals</c> throws on anything but <c>Draft</c>. So running this job twice in a
/// night is harmless, and running it over a month already justified to the insurer is refused
/// rather than silently restating it.
/// </para>
///
/// <para>
/// <b>The commission is earned on premiums COLLECTED, and only there.</b> An <i>adhésion</i> and a
/// <i>résiliation</i> line are census lines: they carry rate 0, so they count on the header
/// without counting twice on the money. A <i>contre-passation</i> carries a NEGATIVE amount and
/// therefore a negative commission, which is what keeps the one property an insurer always checks
/// literally true — the header's commission is the arithmetic sum of the printed lines.
/// </para>
///
/// <para>
/// <b>One currency per statement, by construction.</b> <c>ux_ins_statement_period</c> is
/// <c>(tenant, connection, period)</c> with no currency dimension, so a month's movements in a
/// second currency cannot have their own document. They are EXCLUDED and counted in a warning
/// rather than summed into the total: a bordereau mixing units is the one arithmetic error worse
/// than a missing line.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class GenerateStatementsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<GenerateStatementsJob> logger)
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

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var period = StatementPeriod.PreviousOf(today);

        await GenerateAsync(db, clock, tenantId, period, ct);
    }

    /// <summary>
    /// One period for every insurance connection of the tenant. Internal so the on-demand
    /// regeneration endpoint runs EXACTLY this code: two implementations of a bordereau would
    /// eventually disagree about a figure, and the one on the screen is the one the insurer reads.
    /// </summary>
    internal async Task<int> GenerateAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        Guid tenantId,
        string period,
        CancellationToken ct)
    {
        if (StatementPeriod.Bounds(period) is not var (first, last)) return 0;

        // IgnoreQueryFilters with an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        // INACTIVE connections included, deliberately: an insurer switched off mid-month still
        // earned the institution its commission on the weeks it ran, and a bordereau that
        // vanished with the connection is a month nobody can justify.
        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Family == IntegrationFamily.Insurance)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        var generated = 0;

        foreach (var connection in connections)
        {
            var movements = await CollectAsync(db, tenantId, connection.Id, first, last, ct);

            if (movements.Count == 0) continue;

            // The period's own currency: the one most of its movements are in. Everything else is
            // left out and said so — see the class remarks.
            var currency = movements
                .GroupBy(m => m.Currency)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;

            var excluded = movements.Count(m => !string.Equals(m.Currency, currency, StringComparison.Ordinal));

            if (excluded > 0)
            {
                logger.LogWarning(
                    "Statement {Period} for insurer {Insurer}: {Excluded} movement(s) in a "
                    + "currency other than {Currency} were left out — one statement per insurer "
                    + "per month cannot carry two units",
                    period, connection.Name, excluded, currency);
            }

            var lines = movements
                .Where(m => string.Equals(m.Currency, currency, StringComparison.Ordinal))
                .OrderBy(m => m.OccurredOn)
                .ThenBy(m => m.PolicyNumber, StringComparer.Ordinal)
                .ToList();

            var statement = await db.InsurerStatements
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(
                    s => s.TenantId == tenantId
                      && s.ConnectionId == connection.Id
                      && s.Period == period,
                    ct);

            if (statement is null)
            {
                statement = InsurerStatement.Create(
                    tenantId: tenantId,
                    connectionId: connection.Id,
                    period: period,
                    currency: currency,
                    clock: clock);

                db.InsurerStatements.Add(statement);
            }
            else if (!statement.IsRebuildable)
            {
                // Already justified to the insurer. Refused rather than restated — ApplyTotals
                // would throw anyway, and letting it reach the aggregate would abandon the other
                // insurers of the tenant.
                logger.LogInformation(
                    "Statement {Period} for insurer {Insurer} is {Status} and was not rebuilt",
                    period, connection.Name, statement.Status);
                continue;
            }
            else
            {
                // The draft's previous lines go. Loaded and removed rather than
                // ExecuteDeleteAsync, which EF InMemory does not support — so the rebuild path
                // would otherwise be untested, which is what it cost M12's template activation.
                var stale = await db.InsurerStatementLines
                    .IgnoreQueryFilters()
                    .AsTracking()
                    .Where(l => l.TenantId == tenantId && l.StatementId == statement.Id)
                    .ToListAsync(ct);

                db.InsurerStatementLines.RemoveRange(stale);
            }

            foreach (var movement in lines)
            {
                db.InsurerStatementLines.Add(InsurerStatementLine.Create(
                    tenantId: tenantId,
                    statementId: statement.Id,
                    lineType: movement.LineType,
                    policyId: movement.PolicyId,
                    crmCustomerId: movement.CrmCustomerId,
                    occurredOn: movement.OccurredOn,
                    amount: movement.Amount,
                    currency: movement.Currency,
                    commissionRate: movement.CommissionRate,
                    policyNumber: movement.PolicyNumber,
                    insurerProductCode: movement.InsurerProductCode,
                    clock: clock,
                    instalmentId: movement.InstalmentId,
                    subscriptionId: movement.SubscriptionId,
                    cbsReference: movement.CbsReference));
            }

            // The header is the SUM of the lines just written — never a second computation over
            // the sources, which would be a figure that disagrees with the printed detail.
            statement.ApplyTotals(
                subscriptionCount: lines.Count(l => l.LineType == StatementLineType.Subscription),
                cancellationCount: lines.Count(l => l.LineType == StatementLineType.Cancellation),
                reversalCount: lines.Count(l => l.LineType == StatementLineType.Reversal),
                premiumCollected: lines
                    .Where(l => l.LineType == StatementLineType.PremiumPaid)
                    .Sum(l => l.Amount),
                // Positive, because the header shows what went BACK rather than a negative
                // collection: the two are kept separate on purpose, and the line that carries the
                // sign is the one that has to add up.
                premiumReversed: lines
                    .Where(l => l.LineType == StatementLineType.Reversal)
                    .Sum(l => Math.Abs(l.Amount)),
                commissionAmount: lines.Sum(
                    l => Math.Round(l.Amount * l.CommissionRate, 2, MidpointRounding.ToEven)),
                clock: clock);

            generated++;
        }

        await db.SaveChangesAsync(ct);

        if (generated > 0)
        {
            logger.LogInformation(
                "Statements for tenant {TenantId}, period {Period}: {Generated} bordereau(x) "
                + "built", tenantId, period, generated);
        }

        return generated;
    }

    // ── the sources ─────────────────────────────────────────────────────────

    /// <summary>
    /// Everything that happened for one insurer in one month, as the lines a bordereau carries.
    ///
    /// <para>
    /// Four reads and not one query: the four kinds of movement live in four tables with four
    /// different dates, and a single join would have to decide which of them orders the result.
    /// </para>
    /// </summary>
    private static async Task<List<Movement>> CollectAsync(
        IntegrationDbContext db,
        Guid tenantId,
        Guid connectionId,
        DateOnly first,
        DateOnly last,
        CancellationToken ct)
    {
        // The commission RATE per policy, resolved once. A policy whose product is not in the
        // catalogue — synchronised from the insurer with no mapping — gets ZERO: inventing a rate
        // would bill an insurer a commission nobody configured.
        var policies = await db.Policies
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.ConnectionId == connectionId)
            .GroupJoin(
                db.InsuranceProducts.IgnoreQueryFilters(),
                p => p.ProductId, pr => (Guid?)pr.Id,
                (p, products) => new { Policy = p, Products = products })
            .SelectMany(
                x => x.Products.DefaultIfEmpty(),
                (x, product) => new
                {
                    x.Policy.Id,
                    x.Policy.CrmCustomerId,
                    x.Policy.PolicyNumber,
                    x.Policy.InsurerProductCode,
                    x.Policy.EffectiveDate,
                    x.Policy.PremiumAmount,
                    x.Policy.Currency,
                    x.Policy.Status,
                    x.Policy.StatusChangedAt,
                    x.Policy.SubscriptionId,
                    CommissionRate = product == null ? 0m : product.CommissionRate,
                })
            .ToListAsync(ct);

        if (policies.Count == 0) return [];

        var byId = policies.ToDictionary(p => p.Id);
        var policyIds = policies.Select(p => p.Id).ToList();
        var movements = new List<Movement>();

        // ── adhésions ───────────────────────────────────────────────────────
        //
        // On the EFFECTIVE date and not on CreatedAt: a policy sold at the insurer's counter in
        // March and discovered by ASS-07's sync in June belongs to March's bordereau. CreatedAt
        // says when WE learnt about it, which is not a fact about the contract.
        //
        // A census line: rate 0, so an adhésion counts on the header without earning a commission
        // the collected premium will earn again.
        movements.AddRange(policies
            .Where(p => p.EffectiveDate >= first && p.EffectiveDate <= last)
            .Select(p => new Movement(
                StatementLineType.Subscription, p.Id, p.CrmCustomerId, p.EffectiveDate,
                p.PremiumAmount, p.Currency, CommissionRate: 0m,
                p.PolicyNumber, p.InsurerProductCode, null, p.SubscriptionId, null)));

        // ── résiliations ────────────────────────────────────────────────────
        //
        // Also a census line, and with amount 0: a cancelled contract gives nothing back by
        // itself — the money that goes back is a contre-passation, which has its own line.
        movements.AddRange(policies
            .Where(p => (p.Status == PolicyStatus.Cancelled || p.Status == PolicyStatus.Expired)
                     && DateOnly.FromDateTime(p.StatusChangedAt.UtcDateTime) >= first
                     && DateOnly.FromDateTime(p.StatusChangedAt.UtcDateTime) <= last)
            .Select(p => new Movement(
                StatementLineType.Cancellation, p.Id, p.CrmCustomerId,
                DateOnly.FromDateTime(p.StatusChangedAt.UtcDateTime),
                Amount: 0m, p.Currency, CommissionRate: 0m,
                p.PolicyNumber, p.InsurerProductCode, null, p.SubscriptionId, null)));

        // ── primes encaissées ───────────────────────────────────────────────
        //
        // Where the commission is actually earned. On PaidAt — the day the money moved — and only
        // for an instalment that WAS paid: a debit still in flight is not a collection.
        var paid = await db.PremiumInstalments
            .IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId
                     && policyIds.Contains(i.PolicyId)
                     && i.Status == InstalmentStatus.Paid
                     && i.PaidAt != null)
            .Select(i => new
            {
                i.Id, i.PolicyId, i.Amount, i.Currency, i.CbsDebitReference, i.PaidAt,
            })
            .ToListAsync(ct);

        movements.AddRange(paid
            .Where(i => DateOnly.FromDateTime(i.PaidAt!.Value.UtcDateTime) >= first
                     && DateOnly.FromDateTime(i.PaidAt.Value.UtcDateTime) <= last)
            .Select(i =>
            {
                var policy = byId[i.PolicyId];

                return new Movement(
                    StatementLineType.PremiumPaid, i.PolicyId, policy.CrmCustomerId,
                    DateOnly.FromDateTime(i.PaidAt!.Value.UtcDateTime),
                    i.Amount, i.Currency, policy.CommissionRate,
                    policy.PolicyNumber, policy.InsurerProductCode, i.Id, policy.SubscriptionId,
                    i.CbsDebitReference);
            }));

        // ── contre-passations ───────────────────────────────────────────────
        //
        // ASS-05's reversals: the premium was collected and the insurer refused, so it went back.
        // NEGATIVE amount, therefore negative commission — the header's commission is the
        // arithmetic sum of the lines, and a reversal that kept a positive sign would have the
        // institution bill an insurer for a premium it gave back.
        var reversed = await db.InsuranceSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId
                     && s.ConnectionId == connectionId
                     && s.ReversedAt != null
                     && s.PolicyId != null)
            .Select(s => new
            {
                s.Id, s.PolicyId, s.PremiumAmount, s.Currency, s.CbsReversalReference, s.ReversedAt,
            })
            .ToListAsync(ct);

        movements.AddRange(reversed
            .Where(s => DateOnly.FromDateTime(s.ReversedAt!.Value.UtcDateTime) >= first
                     && DateOnly.FromDateTime(s.ReversedAt.Value.UtcDateTime) <= last
                     && byId.ContainsKey(s.PolicyId!.Value))
            .Select(s =>
            {
                var policy = byId[s.PolicyId!.Value];

                return new Movement(
                    StatementLineType.Reversal, policy.Id, policy.CrmCustomerId,
                    DateOnly.FromDateTime(s.ReversedAt!.Value.UtcDateTime),
                    -s.PremiumAmount, s.Currency, policy.CommissionRate,
                    policy.PolicyNumber, policy.InsurerProductCode, null, s.Id,
                    s.CbsReversalReference);
            }));

        return movements;
    }

    /// <summary>
    /// One movement, already shaped as the line it becomes. A record and not the entity, because
    /// the currency has to be weighed across all four kinds BEFORE any line is written.
    /// </summary>
    private sealed record Movement(
        StatementLineType LineType,
        Guid PolicyId,
        Guid CrmCustomerId,
        DateOnly OccurredOn,
        decimal Amount,
        string Currency,
        decimal CommissionRate,
        string PolicyNumber,
        string InsurerProductCode,
        Guid? InstalmentId,
        Guid? SubscriptionId,
        string? CbsReference);
}
