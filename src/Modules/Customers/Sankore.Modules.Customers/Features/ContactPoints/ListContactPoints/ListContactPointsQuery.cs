using MediatR;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.ListContactPoints;

/// <summary>
/// Reads a client's contact points. A query, so it deliberately does NOT implement
/// <c>ICommand</c>: no transaction, no audit entry (values come back masked, so a
/// listing is not a disclosure).
/// </summary>
public sealed record ListContactPointsQuery(
    Guid ClientId,
    bool IncludeClosed
) : IRequest<Result<IReadOnlyList<ContactPointDto>>>;
