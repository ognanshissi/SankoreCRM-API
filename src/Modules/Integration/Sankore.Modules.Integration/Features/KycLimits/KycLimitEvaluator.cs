namespace Sankore.Modules.Integration.Features.KycLimits;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// One ceiling crossing the watch found worth publishing: which ceiling, how far past it, and the
/// two figures a consumer needs to say so in a notification.
/// </summary>
internal sealed record KycLimitCrossing(
    KycLimitKind Kind,
    KycLimitSeverity Severity,
    decimal Observed,
    decimal Ceiling);

/// <summary>
/// The whole threshold decision of INT-22, as a pure function of the snapshot's two figures and
/// the ceilings M02 hands back.
///
/// <para>
/// <b>It holds no amount and no percentage of its own.</b> The ceilings (250 000 and 500 000 FCFA
/// by default) and the alert threshold (80 % by default) are tenant settings of M02 —
/// <c>simplified-max-balance</c>, <c>simplified-max-monthly-flow</c>, <c>simplified-alert-pct</c>
/// — reached through <see cref="IKycModule.GetLimitsAsync"/>. A literal here would be a second
/// source of truth for a compliance ceiling, and the second one is silently wrong from the first
/// configuration change onwards. There is deliberately no fallback either: a customer whose
/// limits cannot be read is skipped by the caller, never alerted against an assumed ceiling.
/// </para>
/// </summary>
internal static class KycLimitEvaluator
{
    /// <summary>
    /// What to publish for one customer, given the figures of its <c>CbsSnapshot</c>.
    ///
    /// <para>
    /// <b>The two ceilings are watched independently</b> (criterion 1 names both): a customer may
    /// be approaching its balance ceiling while already past its flow ceiling, and the ledger keys
    /// on <c>LimitKind</c> precisely so both can be reported. What is NOT independent is the
    /// severity within one ceiling — see below.
    /// </para>
    ///
    /// <para>
    /// <b>An uncapped customer yields nothing at all.</b> A full-KYC file has no ceiling, and
    /// <see cref="KycLimits.MaxBalance"/> / <see cref="KycLimits.MaxFlow"/> are documented as
    /// meaningless when <see cref="KycLimits.IsCapped"/> is false — comparing against them would
    /// alert the very customers who are already in order.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<KycLimitCrossing> Evaluate(
        decimal totalBalance, decimal monthlyFlow, KycLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!limits.IsCapped) return [];

        var crossings = new List<KycLimitCrossing>(capacity: 2);

        Examine(crossings, KycLimitKind.Balance, totalBalance, limits.MaxBalance, limits.AlertPct);
        Examine(crossings, KycLimitKind.Flow, monthlyFlow, limits.MaxFlow, limits.AlertPct);

        return crossings;
    }

    /// <summary>
    /// One ceiling. At most ONE crossing comes out of it — <b>Exceeded supersedes Approaching</b>
    /// (criterion 3 over criterion 2).
    ///
    /// <para>
    /// A customer past its ceiling is also, arithmetically, past 80 % of it. Publishing both would
    /// hand M02 two upgrade tasks for one customer and M08 two notifications saying different
    /// things about the same figure, and the stronger statement is the one an operator must act
    /// on. So the breach wins and the warning is not published — not even as a separate ledger
    /// row: nobody needs to be told a customer is "approaching" a ceiling they are already over.
    /// </para>
    ///
    /// <para>
    /// <b>At the ceiling exactly is not past it.</b> <see cref="KycLimits.MaxBalance"/> is the
    /// ceiling on the balance AFTER an operation, i.e. the highest value still allowed, so
    /// equality is Approaching and not Exceeded. The same reading makes the alert threshold
    /// inclusive: at exactly 80 % the agent is warned, which is what "at the alert threshold"
    /// says.
    /// </para>
    ///
    /// <para>
    /// A non-positive ceiling or threshold yields nothing. Neither is a ceiling: a zero
    /// <c>MaxBalance</c> would make every customer holding one franc Exceeded, every month, for a
    /// setting that is a misconfiguration rather than a compliance fact. Refusing to read it as a
    /// ceiling is quieter than acting on it, and the tenant's own settings screen is where it gets
    /// fixed.
    /// </para>
    /// </summary>
    private static void Examine(
        List<KycLimitCrossing> into,
        KycLimitKind kind,
        decimal observed,
        decimal ceiling,
        int alertPct)
    {
        if (ceiling <= 0m) return;

        if (observed > ceiling)
        {
            into.Add(new KycLimitCrossing(kind, KycLimitSeverity.Exceeded, observed, ceiling));
            return;
        }

        if (alertPct <= 0) return;

        var threshold = ceiling * alertPct / 100m;

        if (observed >= threshold)
            into.Add(new KycLimitCrossing(kind, KycLimitSeverity.Approaching, observed, ceiling));
    }
}
