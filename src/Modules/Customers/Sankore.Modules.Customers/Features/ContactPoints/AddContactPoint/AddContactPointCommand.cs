using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;

/// <summary>
/// Adds a phone / email / postal address to a client. The value is encrypted at
/// rest and indexed blindly; <see cref="Value"/> is marked sensitive so the audit
/// trail records <c>"***"</c> instead of the clear number.
/// </summary>
public sealed record AddContactPointCommand(
    Guid ClientId,
    ContactPointType Type,
    [property: SensitiveData] string Value,
    string? Label,
    bool MakePrimary
) : IRequest<Result<AddContactPointResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// <paramref name="AlreadyExisted"/> is true when an identical active contact point
/// was already on file (same type, same blind index): the call is idempotent and
/// returns the existing row rather than creating a duplicate.
/// </summary>
public sealed record AddContactPointResult(
    Guid ContactPointId,
    ContactPointType Type,
    string MaskedValue,
    bool IsPrimary,
    DateTimeOffset ValidFrom,
    bool AlreadyExisted);
