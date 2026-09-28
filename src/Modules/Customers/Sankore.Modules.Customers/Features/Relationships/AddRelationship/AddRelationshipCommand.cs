using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.AddRelationship;

/// <summary>
/// Links a client either to another client of the tenant (<see cref="RelatedClientId"/>)
/// or to a person who is not a client (<see cref="ExternalFullName"/> and friends) —
/// exactly one of the two. The external party's phone and document number are marked
/// sensitive so the audit trail stores <c>"***"</c>.
/// </summary>
public sealed record AddRelationshipCommand(
    Guid ClientId,
    RelationshipType Type,
    Guid? RelatedClientId,
    string? ExternalFullName,
    [property: SensitiveData] string? ExternalPhoneNumber,
    DateOnly? ExternalDateOfBirth,
    [property: SensitiveData] string? ExternalDocumentNumber
) : IRequest<Result<AddRelationshipResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// <paramref name="ReciprocalRelationshipId"/> is set for a client-to-client
/// <c>Spouse</c> link: the mirror row created on the other client.
/// <paramref name="DependentsCount"/> is the client's recomputed dependents count.
/// </summary>
public sealed record AddRelationshipResult(
    Guid RelationshipId,
    Guid? ReciprocalRelationshipId,
    int DependentsCount);
