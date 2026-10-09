namespace Sankore.Modules.Integration.Features.CallLog;

using Sankore.Shared.Kernel;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The two things both read slices of the journal must agree on: the time window, and how an
/// error-family filter is parsed.
///
/// <para>
/// Shared at the area level rather than duplicated per slice because a disagreement is silent:
/// a list defaulting to seven days next to statistics defaulting to one would have an operator
/// comparing a p95 "over the period shown" against a different period, and neither screen would
/// say so.
/// </para>
/// </summary>
internal static class CallLogWindow
{
    /// <summary>
    /// What an unbounded request becomes.
    ///
    /// <para>
    /// <c>integration_call_log</c> is <c>PARTITION BY RANGE (at)</c>, one partition per month.
    /// Partition pruning needs a predicate on <c>at</c>; without one the planner includes every
    /// partition the table has ever accumulated, so an operator opening the screen with no
    /// filters would, after two years, scan twenty-four partitions to show twenty rows — and the
    /// cost would keep growing with the deployment's age rather than with the answer's size.
    /// </para>
    ///
    /// <para>
    /// Seven days rather than a refusal: this is the screen somebody opens when a connection is
    /// misbehaving right now, and a 400 asking them to pick dates first is a worse answer than a
    /// week of history. The resolved window is returned in the response so the default is visible
    /// rather than assumed.
    /// </para>
    /// </summary>
    internal const int DefaultWindowDays = 7;

    /// <summary>
    /// Fills in whichever bound is missing. A <c>From</c> alone runs to now; a <c>To</c> alone
    /// runs back seven days from it — never to the beginning of the table, which is the whole
    /// point.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Resolve(
        DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        var upper = to ?? now;
        var lower = from ?? upper.AddDays(-DefaultWindowDays);

        // An inverted range is the caller's slip, not an empty result worth puzzling over: swap it
        // so the screen shows the period they clearly meant.
        return lower <= upper ? (lower, upper) : (upper, lower);
    }

    /// <summary>
    /// Parses the family filter. Refused by name on an unknown value — see the remark on
    /// <c>ListCallLogQuery.ErrorFamily</c>: an ignored filter answers with the unfiltered list,
    /// which reads as evidence of absence.
    /// </summary>
    internal static Result<ErrorFamily?> ParseFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Result.Ok<ErrorFamily?>(null);

        if (!Enum.TryParse<ErrorFamily>(value, ignoreCase: true, out var parsed))
            return Result.Fail<ErrorFamily?>(
                $"INTEGRATION_ERROR_FAMILY_UNKNOWN: '{value}' is not an error family "
                + $"({string.Join(", ", Enum.GetNames<ErrorFamily>())}).");

        return Result.Ok<ErrorFamily?>(parsed);
    }
}
