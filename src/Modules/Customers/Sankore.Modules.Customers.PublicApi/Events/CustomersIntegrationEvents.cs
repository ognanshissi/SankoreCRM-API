namespace Sankore.Modules.Customers.PublicApi.Events;

using Sankore.Shared.Kernel;

// ─────────────────────────────────────────────────────────────────────────────
// Integration events published by module M01 (Customers) through its outbox.
//
// Contract rules for every record in this file:
//   * `: IntegrationEventBase` — EventId + OccurredAt come for free.
//   * `Guid TenantId` is ALWAYS the first field: consumers read it before
//     anything else to establish their tenant scope (IgnoreQueryFilters +
//     manual predicate).
//   * NO sensitive value ever travels on an integration event. Only identifiers,
//     enum names, counts and field NAMES. Anything encrypted at rest stays
//     behind the audited reveal endpoint.
//   * Enum-typed concepts are carried as strings so consumers never need to
//     reference the Customers domain assembly.
//   * Delivery is at-least-once (outbox + broker) — every consumer must be
//     idempotent, typically via the module's own inbox guard.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A new client record was created (individual or legal entity).</summary>
public sealed record ClientCreatedEvent(
    Guid TenantId,
    Guid ClientId,
    string ClientNumber,
    string ClientType,
    Guid AgencyId,
    Guid? AdvisorUserId,
    Guid? SourceLeadId,
    Guid CreatedBy) : IntegrationEventBase;

/// <summary>The client became Active (KYC approved, or reactivated).</summary>
public sealed record ClientActivatedEvent(
    Guid TenantId,
    Guid ClientId) : IntegrationEventBase;

/// <summary>The client was suspended. <paramref name="Reason"/> is an operator-entered motive, never a sensitive value.</summary>
public sealed record ClientSuspendedEvent(
    Guid TenantId,
    Guid ClientId,
    string Reason,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>The client was archived (soft end of life; the record becomes read-only).</summary>
public sealed record ClientArchivedEvent(
    Guid TenantId,
    Guid ClientId,
    string Reason,
    Guid ActorUserId,
    DateTimeOffset ArchivedAt) : IntegrationEventBase;

/// <summary>The client's owning agency changed (portfolio transfer).</summary>
public sealed record ClientTransferredEvent(
    Guid TenantId,
    Guid ClientId,
    Guid FromAgencyId,
    Guid ToAgencyId,
    Guid? AdvisorUserId,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>
/// One or more protected identity fields changed.
/// <paramref name="ChangedFields"/> carries field NAMES ONLY — never the old or new
/// values, neither in clear text nor encrypted. Consumers that need the value must
/// call the audited reveal endpoint themselves.
/// </summary>
public sealed record ClientSensitiveDataChangedEvent(
    Guid TenantId,
    Guid ClientId,
    IReadOnlyList<string> ChangedFields,
    string Reason,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>
/// Two client records were merged. Every module holding a reference to
/// <paramref name="AbsorbedClientId"/> must repoint it to <paramref name="SurvivorClientId"/>.
/// </summary>
public sealed record ClientsMergedEvent(
    Guid TenantId,
    Guid SurvivorClientId,
    Guid AbsorbedClientId,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>The beneficial-owner structure of a legal client changed (AML relevant).</summary>
public sealed record BeneficialOwnersChangedEvent(
    Guid TenantId,
    Guid LegalClientId,
    int ActiveOwnerCount,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>
/// A guarantor was attached to a client. Exactly one of
/// <paramref name="GuarantorClientId"/> (an existing client) or
/// <paramref name="GuarantorExternalName"/> (a third party not in the base) is set.
/// </summary>
public sealed record GuarantorLinkedEvent(
    Guid TenantId,
    Guid ClientId,
    Guid? GuarantorClientId,
    string? GuarantorExternalName,
    Guid ActorUserId) : IntegrationEventBase;

/// <summary>A solidarity group, tontine or VSLA was created.</summary>
public sealed record GroupCreatedEvent(
    Guid TenantId,
    Guid GroupId,
    string GroupType,
    string Name,
    Guid AgencyId) : IntegrationEventBase;

/// <summary>
/// A membership changed. <paramref name="Change"/> is one of "Joined", "Left" or
/// "RoleChanged"; <paramref name="OfficeRole"/> is the resulting office role name.
/// </summary>
public sealed record GroupMembershipChangedEvent(
    Guid TenantId,
    Guid GroupId,
    Guid ClientId,
    string Change,
    string OfficeRole) : IntegrationEventBase;

/// <summary>The group was dissolved; no further membership change is possible.</summary>
public sealed record GroupDissolvedEvent(
    Guid TenantId,
    Guid GroupId,
    string Reason) : IntegrationEventBase;

/// <summary>The client's commercial segment changed. <paramref name="PreviousSegment"/> is null on first assignment.</summary>
public sealed record ClientSegmentChangedEvent(
    Guid TenantId,
    Guid ClientId,
    string? PreviousSegment,
    string NewSegment) : IntegrationEventBase;

/// <summary>
/// A group fell below its configured minimum size — an alert, not a state change:
/// the group keeps its current status until an operator acts.
/// </summary>
public sealed record ClientUnderMinimumGroupSizeEvent(
    Guid TenantId,
    Guid GroupId,
    int ActiveMembers,
    int MinimumSize) : IntegrationEventBase;

/// <summary>
/// A user revealed more sensitive fields within the rolling window than the tenant
/// allows — a security signal for the compliance officer. Carries counters only.
/// </summary>
public sealed record SensitiveRevealThresholdExceededEvent(
    Guid TenantId,
    Guid ActorUserId,
    int RevealsInWindow,
    int Threshold) : IntegrationEventBase;

/// <summary>
/// The client's personal data was irreversibly anonymized after the retention period.
/// Consumers holding a denormalized copy of any personal field must purge it.
/// </summary>
public sealed record ClientAnonymizedEvent(
    Guid TenantId,
    Guid ClientId,
    DateTimeOffset AnonymizedAt) : IntegrationEventBase;
