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

/// <summary>
/// The client's household became politically exposed when it was not (BNF-B-13).
///
/// <para>
/// A RISE only. Firing on a fall would ask a compliance officer to re-review a file because a
/// mandate expired, which is the opposite of what the story wants — and it is why
/// <c>Client.RecalculateHasPepExposure</c> returns a boolean rather than raising on every change.
/// </para>
///
/// <para>
/// It carries no detail of the exposure: the function, the country and the family link are behind
/// a permission, and the consumer's job is to open a review, not to read the classification.
/// </para>
/// </summary>
public sealed record ClientPepExposureRaisedDomainEvent(Guid ClientId, Guid TenantId)
    : DomainEventBase;
