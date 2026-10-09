namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The bridge between a CRM entity and its identifier in one external system (INT-07).
///
/// <para>
/// Written in the SAME transaction as the command's move to <c>Succeeded</c>. That is the whole
/// correctness argument: a success recorded without its reference leaves a customer that exists
/// in the CBS and that SANKORE can no longer address, so every later call would try to create it
/// again.
/// </para>
///
/// <para>
/// Unique in both directions, PER CONNECTION: the same customer legitimately holds one reference
/// per external system, and the two insurers of a tenant assign their own numbering.
/// </para>
/// </summary>
public sealed class IntegrationReference : AggregateRoot
{
    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The connection's kind, denormalised. Not redundant in practice: the reconciliation and
    /// the snapshot read references without joining the connection, and a kind read from the row
    /// keeps those queries a single table scan.
    /// </summary>
    public PublicApi.IntegrationKind Kind { get; private set; }

    public string EntityType { get; private set; } = string.Empty;

    public Guid CrmId { get; private set; }

    public string ExternalId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Last time the synchronisation refreshed this entity. Null until it has run.</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    private IntegrationReference() { }

    public static IntegrationReference Create(
        Guid tenantId,
        Guid connectionId,
        PublicApi.IntegrationKind kind,
        string entityType,
        Guid crmId,
        string externalId,
        TimeProvider clock,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (crmId == Guid.Empty) throw new DomainException("CrmId is required.");
        if (string.IsNullOrWhiteSpace(entityType)) throw new DomainException("EntityType is required.");
        if (string.IsNullOrWhiteSpace(externalId))
            throw new DomainException("An external id is required — a reference to nothing is worse than none.");

        return new IntegrationReference
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Kind = kind,
            EntityType = entityType.Trim(),
            CrmId = crmId,
            ExternalId = externalId.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
    }

    public void MarkSynced(TimeProvider clock) => LastSyncedAt = clock.GetUtcNow();
}
