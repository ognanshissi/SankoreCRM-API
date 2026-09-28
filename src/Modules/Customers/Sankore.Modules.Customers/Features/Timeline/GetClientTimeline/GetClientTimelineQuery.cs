namespace Sankore.Modules.Customers.Features.Timeline.GetClientTimeline;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads one client's unified timeline (US-M01-BE-26). A QUERY: it does not implement
/// <c>ICommand</c>, so it skips the transaction and audit behaviors — reading a timeline is
/// not an audited act (unlike revealing a sensitive field).
/// </summary>
/// <param name="ClientId">Client whose timeline is read.</param>
/// <param name="SourceModule">Optional exact filter on the producing module, e.g. <c>Customers</c>, <c>Leads</c>.</param>
/// <param name="EntryType">Optional exact filter on the entry type, e.g. <c>CLIENT_ACTIVATED</c>.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Items per page; clamped to 100 by the handler.</param>
public sealed record GetClientTimelineQuery(
    Guid ClientId,
    string? SourceModule = null,
    string? EntryType = null,
    int Page = 1,
    int PageSize = 20) : IRequest<Result<PagedResult<ClientTimelineEntryDto>>>;

/// <summary>
/// One timeline row as exposed over HTTP. Contains no sensitive value by construction — see
/// <c>TimelineSummaryGuard</c>.
/// </summary>
public sealed record ClientTimelineEntryDto(
    Guid Id,
    string SourceModule,
    string EntryType,
    DateTimeOffset OccurredAt,
    string Summary,
    string? ReferenceType,
    string? ReferenceId);
