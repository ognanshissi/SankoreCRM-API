namespace Sankore.Modules.Integration.Features.Insurance.Statements;

using System.Globalization;

/// <summary>
/// The month a statement covers, as the one place that turns a date into <c>YYYY-MM</c> and back.
///
/// <para>
/// A helper and not two lines inlined in the job, because the generation, the regeneration guard
/// and the read endpoints all have to agree about which days belong to a period — and
/// <c>InsurerStatement.Create</c> refuses anything but seven characters, so a caller that formats
/// it differently fails at the aggregate rather than at the request.
/// </para>
///
/// <para>
/// <b><see cref="CultureInfo.InvariantCulture"/> on both sides, and that is load-bearing.</b> A
/// period rendered with the process culture would key the same month differently on a host with
/// non-Gregorian or non-ASCII digits — the exact class of bug this repository already pays for in
/// its spreadsheet readers and its idempotency keys.
/// </para>
/// </summary>
internal static class StatementPeriod
{
    internal const string Format = "yyyy-MM";

    /// <summary>The period a day belongs to.</summary>
    internal static string For(DateOnly day)
        => day.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// The month BEFORE the one containing <paramref name="today"/> — what a statement generated
    /// on the 1st is about. A statement for the running month would be a document whose figures
    /// change every night, which is not something to justify to an insurer.
    /// </summary>
    internal static string PreviousOf(DateOnly today)
        => For(new DateOnly(today.Year, today.Month, 1).AddMonths(-1));

    /// <summary>
    /// The first and last day of a period, inclusive. <c>null</c> when the string is not a month —
    /// a caller-supplied period reaches this from a query parameter.
    /// </summary>
    internal static (DateOnly First, DateOnly Last)? Bounds(string? period)
    {
        if (string.IsNullOrWhiteSpace(period)) return null;

        if (!DateTime.TryParseExact(
                period.Trim(), Format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return null;

        var first = new DateOnly(parsed.Year, parsed.Month, 1);

        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Whether a caller-supplied string is a month at all.</summary>
    internal static bool IsValid(string? period) => Bounds(period) is not null;
}
