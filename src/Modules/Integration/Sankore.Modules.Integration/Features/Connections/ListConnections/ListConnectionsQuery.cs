namespace Sankore.Modules.Integration.Features.Connections.ListConnections;

using MediatR;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's connections, filtered by family and by activity.
///
/// <para>
/// A query: it does NOT implement <c>ICommand</c>, so it is wrapped by neither
/// <c>TransactionBehavior</c> nor <c>AuditBehavior</c>. Reading the list of configured systems is
/// not an event worth an audit row.
/// </para>
/// </summary>
internal sealed record ListConnectionsQuery(
    IntegrationFamily? Family = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 50)
    : IRequest<Result<PagedResult<ConnectionListDto>>>;
