namespace Sankore.Modules.Integration.Features.CallLog.GetCallLogStats;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// "Is the CBS slow today, and which operation is it" (INT-08). A query — no <c>ICommand</c>.
///
/// <para>
/// This is what turns the journal from an archive into something an operator uses. A paginated
/// list of calls answers "what happened to this command"; it cannot answer "our agents say
/// account opening is crawling since noon", because that question is about a distribution. Per
/// operation, because a core banking system is rarely slow as a whole — one endpoint degrades,
/// and an average over every operation hides it behind the health check that answers in 20 ms.
/// </para>
/// </summary>
/// <param name="From">
/// Same default as the list and for the same reason: seven days when neither bound is given,
/// because the table is partitioned by month and an unbounded aggregate reads every partition.
/// Pass <c>from</c> = this morning for the "slow today" question.
/// </param>
internal sealed record GetCallLogStatsQuery(
    Guid? ConnectionId,
    string? ErrorFamily,
    DateTimeOffset? From,
    DateTimeOffset? To) : IRequest<Result<CallLogStats>>;

/// <param name="Operations">
/// Busiest first. An operation with no call in the window is absent rather than present with
/// zeroes: this module cannot enumerate the operations an adapter supports — the capability
/// matrix is per adapter and per installation — so a zero row would be an invention.
/// </param>
public sealed record CallLogStats(
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalCalls,
    int FailedCalls,
    IReadOnlyList<CallLogOperationStats> Operations);

/// <param name="P95DurationMs">
/// Nearest-rank 95th percentile: the duration of the call at position <c>ceil(0.95 × n)</c> of
/// the window's calls for this operation, sorted ascending. A real observed duration and not an
/// interpolation, so it can always be looked up as an actual row.
///
/// <para>
/// The p95 is here rather than only an average because the average is the number that lies about
/// a back-office: a CBS answering 95 calls in 40 ms and five in 30 s averages 1.5 s, which looks
/// tolerable and describes nobody's experience. The five agents who waited half a minute are the
/// complaint.
/// </para>
/// </summary>
/// <param name="Failures">
/// Calls with any error family. Broken out by family underneath, because the three mean different
/// actions: transient is the back-office's availability, functional is a business refusal that
/// needs a human, technical is OUR configuration.
/// </param>
public sealed record CallLogOperationStats(
    string Operation,
    int Calls,
    int Failures,
    int TransientFailures,
    int FunctionalFailures,
    int TechnicalFailures,
    long AvgDurationMs,
    long P95DurationMs,
    long MaxDurationMs);
