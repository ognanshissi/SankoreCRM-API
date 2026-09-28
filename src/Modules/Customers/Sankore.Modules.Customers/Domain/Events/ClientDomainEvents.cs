namespace Sankore.Modules.Customers.Domain.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// In-process domain events raised by the Customers aggregates themselves.
/// They never leave the module: the feature slices translate the ones that matter
/// into <c>IntegrationEventBase</c> records published through the outbox.
/// </summary>
public sealed record ClientCreatedDomainEvent(Guid ClientId) : DomainEventBase;

public sealed record ClientStatusChangedDomainEvent(
    Guid ClientId, ClientStatus From, ClientStatus To, string? Reason) : DomainEventBase;

public sealed record ClientSensitiveFieldsChangedDomainEvent(
    Guid ClientId, IReadOnlyList<string> Fields) : DomainEventBase;

public sealed record ClientTransferredDomainEvent(
    Guid ClientId, Guid FromAgencyId, Guid ToAgencyId) : DomainEventBase;

public sealed record ClientMergedDomainEvent(Guid AbsorbedId, Guid SurvivorId) : DomainEventBase;

public sealed record GroupCreatedDomainEvent(Guid GroupId) : DomainEventBase;

public sealed record GroupMembershipChangedDomainEvent(
    Guid GroupId, Guid ClientId, string Change) : DomainEventBase;

public sealed record GroupDissolvedDomainEvent(Guid GroupId) : DomainEventBase;
