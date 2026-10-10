namespace Sankore.Modules.Integration.Features.Commands.ListCommands;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The rejection queue (INT-05, criterion 5; INT-11's <c>Integration.Command.View</c>).
///
/// <para>
/// A query — no <c>ICommand</c>. It has to be USABLE, which is why it filters on exactly the
/// three things an operator works from: the status (what is stuck), the error family (whether the
/// IMF has something to fix or the external system refused), and the entity (what one customer
/// owes). A screen that could only list everything by date would be a log, and nobody works a
/// log.
/// </para>
/// </summary>
/// <param name="Status">
/// One <c>CommandStatus</c> name, case-insensitive. An unknown value is refused by name rather
/// than ignored: silently dropping the filter answers with the unfiltered list, which reads as
/// "there is nothing in that state".
/// </param>
/// <param name="ErrorFamily">
/// One <c>ErrorFamily</c> name. <c>Technical</c> alone is the administrator's worklist — those
/// are the rejections a configuration change fixes.
/// </param>
internal sealed record ListIntegrationCommandsQuery(
    string? Status,
    string? ErrorFamily,
    string? CommandType,
    string? EntityType,
    Guid? CrmId,
    Guid? ConnectionId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize) : IRequest<Result<IntegrationCommandListPage>>;

internal sealed record IntegrationCommandListPage(
    IReadOnlyList<IntegrationCommandListItem> Rows,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
}

/// <summary>
/// One row of the queue. No payload and no payload field names: a list is read by anyone holding
/// <c>Integration.Command.View</c>, and what a command carried is answered by the detail read.
/// </summary>
/// <param name="ErrorCode">
/// The stable code, split back out of the stored <c>CODE: detail</c> message, so the front can
/// branch and translate. <paramref name="ErrorDetail"/> is the operator-facing remainder.
/// </param>
internal sealed record IntegrationCommandListItem(
    Guid CommandId,
    Guid ConnectionId,
    string CommandType,
    string EntityType,
    Guid CrmId,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string? ErrorFamily,
    string? ErrorCode,
    string? ErrorDetail,
    string? ExternalResponseRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
