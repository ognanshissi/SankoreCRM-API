namespace Sankore.Modules.Integration.Features.Connections.GetConnection;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// One connection, settings included. A query: no <c>ICommand</c>, so neither the transaction nor
/// the audit behaviour wraps it.
/// </summary>
internal sealed record GetConnectionQuery(Guid ConnectionId)
    : IRequest<Result<ConnectionDetailDto>>;
