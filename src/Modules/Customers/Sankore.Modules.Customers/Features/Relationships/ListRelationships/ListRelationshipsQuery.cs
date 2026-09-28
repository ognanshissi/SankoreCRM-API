using MediatR;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.ListRelationships;

/// <summary>
/// Reads a client's relationships. A query: no <c>ICommand</c>, so no transaction and
/// no audit entry — external values come back masked, so a listing discloses nothing.
/// </summary>
public sealed record ListRelationshipsQuery(
    Guid ClientId,
    bool IncludeClosed
) : IRequest<Result<IReadOnlyList<RelationshipDto>>>;
