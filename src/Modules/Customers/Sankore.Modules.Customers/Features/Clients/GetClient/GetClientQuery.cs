namespace Sankore.Modules.Customers.Features.Clients.GetClient;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-10 — the client detail screen.
///
/// A query, so it does NOT implement <c>ICommand</c>: no transaction and no audit entry.
/// Opening a client record is not an event worth auditing; REVEALING one of its protected
/// fields is, and that is a separate, audited command.
/// </summary>
public sealed record GetClientQuery(Guid ClientId) : IRequest<Result<ClientDetailDto>>;
