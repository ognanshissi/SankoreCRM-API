namespace Sankore.Modules.Integration.Features.CallLog.ListCallLog;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The call journal, paginated (INT-08). A query — no <c>ICommand</c>, so it is neither
/// transacted nor audited: reading a journal of calls is not itself an event worth a second
/// journal, and the audit table is kept meaningful by holding only mutations.
/// </summary>
/// <param name="From">
/// Start of the window, inclusive. <b>Effectively mandatory.</b> <c>integration_call_log</c> is
/// partitioned monthly by range on <c>at</c>, and a query with no bound on <c>at</c> cannot be
/// pruned: PostgreSQL plans it across <i>every</i> partition that exists, including the months
/// nobody is asking about, and the cost grows for the life of the deployment rather than with the
/// size of the answer. The handler therefore substitutes
/// <c>DefaultWindowDays</c> when neither bound is given — a default window, not an unbounded
/// scan, because the alternative is a screen that gets slower every month and a 400 on the
/// first page an operator ever opens.
/// </param>
/// <param name="To">End of the window, inclusive. Defaults to now.</param>
/// <param name="ConnectionId">
/// Narrows to one connection. Cross-tenant isolation is not this parameter's job — the context's
/// query filter answers an id from another tenant with an empty page, which tells the caller
/// nothing about whether that connection exists.
/// </param>
/// <param name="CommandId">
/// Every call made for one queued write, which is how the rejection-queue screen explains a
/// <c>Rejected</c> command: three transient rows and then a functional one.
/// </param>
/// <param name="ErrorFamily">
/// <c>Transient</c>, <c>Functional</c> or <c>Technical</c>. Unparsable values are refused by name
/// rather than ignored: silently dropping the filter would answer with the unfiltered list, which
/// reads as "there were no technical failures".
/// </param>
internal sealed record ListCallLogQuery(
    Guid? ConnectionId,
    Guid? CommandId,
    string? Operation,
    string? ErrorFamily,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize) : IRequest<Result<CallLogPage>>;

/// <param name="From">
/// The window actually used, resolved. Returned so the screen can state the period it is
/// showing — a default the caller did not pass is otherwise invisible, and "no calls" means
/// something very different over seven days than over a year.
/// </param>
public sealed record CallLogPage(
    IReadOnlyList<CallLogRow> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    DateTimeOffset From,
    DateTimeOffset To)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
}

/// <summary>
/// One journalled call, in full. Every column of <c>integration_call_log</c> is returned because
/// the table holds no payload and no personal data by construction — that is what makes this
/// endpoint safe to expose to an operator and the table safe to hand to a controller. A test
/// pins the column set by reflection, so a future column cannot quietly widen what this
/// response carries.
/// </summary>
/// <param name="Endpoint">
/// Path only. The entity drops the query string and the fragment on write, so what is stored —
/// and therefore what is returned here — can never carry the identifier an adapter put in a
/// query parameter.
/// </param>
/// <param name="ErrorCode">
/// A stable code, bounded to 80 characters, never a response body. Phrasing belongs to the
/// screen, which holds this product's French labels.
/// </param>
public sealed record CallLogRow(
    Guid Id,
    DateTimeOffset At,
    Guid ConnectionId,
    Guid? CommandId,
    string Operation,
    string? Endpoint,
    int? HttpStatus,
    long DurationMs,
    string? ErrorFamily,
    string? ErrorCode,
    string? CorrelationId);
