namespace Sankore.Modules.Integration.Features.Insurance.Premiums;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Turns a policy's periodicity into the DUE DATES its instalments fall on (ASS-08).
///
/// <para>
/// <b>Why dates and not a count.</b> The schedule is materialised one row per due date, which is
/// what <c>ux_ins_premium_instalment_due (tenant_id, policy_id, due_date)</c> keys on — so the
/// generator has to produce the same date for the same instalment every time it runs, or a second
/// run inserts duplicates. Computing from the policy's own effective date rather than from "today
/// plus n" is what makes that true after an interrupted night.
/// </para>
///
/// <para>
/// <b>A pure function, deliberately.</b> No database, no clock: every edge case below is a unit
/// test over a date, which is the only way the day-of-month rule gets looked at.
/// </para>
/// </summary>
internal static class PremiumSchedule
{
    /// <summary>
    /// The due dates of <paramref name="periodicity"/> from <paramref name="effectiveDate"/>,
    /// up to and including <paramref name="until"/>.
    ///
    /// <para>
    /// The FIRST instalment falls on the effective date itself: a policy that starts today owes
    /// its first premium today, and a schedule that began one period later would give the customer
    /// a free month of cover.
    /// </para>
    ///
    /// <para>
    /// <see cref="PremiumPeriodicity.Single"/> yields exactly one date, whatever the horizon — a
    /// one-off premium has no schedule, and generating one would debit it again every year.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DateOnly> DueDates(
        DateOnly effectiveDate,
        DateOnly until,
        PremiumPeriodicity periodicity,
        DateOnly? expiryDate = null)
    {
        if (until < effectiveDate) return [];

        // The policy's own end, when it has one: a schedule that ran past the expiry would debit
        // a premium for cover the customer no longer has.
        var horizon = expiryDate is { } expiry && expiry < until ? expiry : until;

        if (horizon < effectiveDate) return [];

        var months = MonthsBetweenInstalments(periodicity);

        if (months is null) return [effectiveDate];

        var dates = new List<DateOnly>();

        for (var index = 0; ; index++)
        {
            // From the ANCHOR each time, never from the previous date: adding a month twelve
            // times to 31 January walks to the 28th and stays there, so a policy anchored on the
            // 31st would silently move to the 28th for ever. Anchoring makes every instalment
            // fall on the anchor's day of month, or on the last day of a shorter month.
            var due = effectiveDate.AddMonths(index * months.Value);

            if (due > horizon) break;

            dates.Add(due);

            // A guard against a periodicity of zero months, which MonthsBetweenInstalments cannot
            // return today. Without it a future value would loop for ever inside a nightly job.
            if (months.Value <= 0) break;
        }

        return dates;
    }

    /// <summary>
    /// Months between two instalments, or <c>null</c> for a one-off premium.
    ///
    /// <para>
    /// A switch with no default, so adding a periodicity to the enum is a compile error here
    /// rather than a schedule that silently generates one instalment and stops.
    /// </para>
    /// </summary>
    internal static int? MonthsBetweenInstalments(PremiumPeriodicity periodicity)
        => periodicity switch
        {
            PremiumPeriodicity.Single => null,
            PremiumPeriodicity.Monthly => 1,
            PremiumPeriodicity.Quarterly => 3,
            PremiumPeriodicity.SemiAnnual => 6,
            PremiumPeriodicity.Annual => 12,
            _ => throw new ArgumentOutOfRangeException(
                nameof(periodicity), periodicity,
                "An unmapped periodicity would generate no schedule at all, which reads as a "
                + "paid-up policy. Add it here when the enum grows."),
        };
}
