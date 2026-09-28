namespace Sankore.Modules.Customers.Features.Lifecycle.GetStatusHistory;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Paginated status history of a client (US-M01-BE-13/14/15/16): who changed the
/// status, from what to what, why and when.
/// A read: it deliberately does NOT implement <c>ICommand</c>, so neither
/// TransactionBehavior nor AuditBehavior wraps it (unlike the reveal slice, which
/// exposes clear-text data and must be audited).
/// </summary>
public sealed record GetStatusHistoryQuery(Guid ClientId, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedResult<ClientStatusHistoryDto>>>;

/// <summary>
/// One transition. Enum values travel as names; <c>OldStatus</c> is null for the
/// very first line (record creation). No sensitive value: <c>Reason</c> is the
/// operator motive already carried by the corresponding integration event.
/// </summary>
public sealed record ClientStatusHistoryDto(
    Guid Id,
    string? OldStatus,
    string NewStatus,
    string? Reason,
    Guid ActorUserId,
    DateTimeOffset OccurredAt);
