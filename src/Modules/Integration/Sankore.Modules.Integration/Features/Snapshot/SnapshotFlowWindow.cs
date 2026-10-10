namespace Sankore.Modules.Integration.Features.Snapshot;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which calendar months <c>cbs_customer_snapshot.monthly_flow</c> adds up.
///
/// <para>
/// <b>The width of the window is M02's setting, never a literal here.</b> It comes from
/// <c>IKycModule.GetLimitsAsync().WindowDays</c> — the same <c>kyc_settings</c> row
/// (<c>simplified-flow-window-days</c>) the ceiling of INT-22 is measured against. A 30 written
/// into this module would be a second copy of a tenant-configurable compliance parameter, and the
/// two would drift the first time a tenant changed it: the figure shown next to the ceiling would
/// no longer be the figure the ceiling applies to, which is worse than either being wrong on its
/// own.
/// </para>
///
/// <para>
/// The window rounds OUT to whole months, and that is a property of the port rather than a choice:
/// <c>ICbsTransactionPort.GetMonthlyFlowAsync</c> takes a <see cref="YearMonth"/>, so a 90-day
/// window can only be read as the four calendar months it touches. The figure is therefore an
/// upper bound on the window — which is the safe direction for something compared against a
/// ceiling, and the reason the alternative (prorating a month we never measured) was not taken.
/// </para>
/// </summary>
internal static class SnapshotFlowWindow
{
    /// <summary>
    /// The months to ask for, oldest first, for a window of <paramref name="windowDays"/> days
    /// ending on <paramref name="now"/>.
    ///
    /// <para>
    /// <paramref name="windowDays"/> is null when M02 has no file for the customer and so no
    /// ceiling to measure against. The fallback is the CURRENT calendar month — not a hidden
    /// thirty days: the column is called <c>monthly_flow</c>, the port answers per
    /// <see cref="YearMonth"/>, and one month is that definition rather than a number this module
    /// invented.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<YearMonth> MonthsEndingAt(DateTimeOffset now, int? windowDays)
    {
        var end = DateOnly.FromDateTime(now.UtcDateTime);

        // Inclusive of today: a window of one day is today, not today and yesterday.
        var start = windowDays is > 0
            ? end.AddDays(-(windowDays.Value - 1))
            : new DateOnly(end.Year, end.Month, 1);

        var months = new List<YearMonth>();

        for (var cursor = new DateOnly(start.Year, start.Month, 1); cursor <= end; cursor = cursor.AddMonths(1))
            months.Add(YearMonth.From(cursor));

        return months;
    }
}
