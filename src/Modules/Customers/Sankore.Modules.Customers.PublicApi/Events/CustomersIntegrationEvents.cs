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

/// <summary>
/// A client's household became politically exposed when it was not (BNF-B-13) — the trigger for a
/// KYC review at raised vigilance.
///
/// <para>
/// A RISE only. Publishing on a fall would ask a compliance officer to re-review a file because a
/// mandate expired, which is the opposite of the intent.
/// </para>
///
/// <para>
/// It carries NO detail of the exposure: the function, the country and the family link sit behind
/// <c>customers:pep_read</c>, and the consumer's job is to open a review, not to read the
/// classification. <paramref name="DeclaredBy"/> is the authenticated agent, so the review names an
/// accountable actor rather than a SYSTEM placeholder.
/// </para>
/// </summary>
public sealed record ClientPepExposureRaisedEvent(
    Guid TenantId,
    Guid ClientId,
    Guid DeclaredBy) : IntegrationEventBase;

/// <summary>
/// A client subscribed to a product through the CRM and the holding is now signed and queued for
/// its back office (BNF-B-14/B-16).
///
/// <para>
/// IDS ONLY, deliberately: the terms, the signature reference and the designations all stay in M01,
/// and an outbox row carrying them would be a second copy of a contract that nothing purges. The
/// consumer reads what it needs through the module's contract.
/// </para>
/// </summary>
/// <param name="ClauseVersion">
/// Which version of the beneficiary clause is being sent. Part of the idempotency key a back-office
/// command is built from, because an avenant is a second send for the same holding.
/// </param>
public sealed record ProductSubscriptionRequestedEvent(
    Guid TenantId,
    Guid ClientId,
    Guid HoldingId,
    Guid ProductLineId,
    string ProductCode,
    int ClauseVersion,
    Guid RequestedBy) : IntegrationEventBase;

/// <summary>
/// The beneficiary clause of a holding changed (BNF-B-15/B-16). Ids and the new version only.
/// </summary>
/// <param name="IsAmendment">
/// True when the holding was already <c>Active</c>, so this is an avenant to a live contract rather
/// than a clause still being captured. The consumer needs it: the first reaches a back office as a
/// modification of an existing policy, the second travels with the subscription itself.
/// </param>
public sealed record BeneficiaryDesignationChangedEvent(
    Guid TenantId,
    Guid ClientId,
    Guid HoldingId,
    int ClauseVersion,
    int DesignationCount,
    bool IsAmendment,
    Guid RecordedBy) : IntegrationEventBase;

/// <summary>
/// A household member was removed and left beneficiary designations behind (BNF-B-13) — the agent
/// has to decide what becomes of them.
///
/// <para>
/// Published ONLY when designations were actually flagged. A removal that affects nothing is
/// ordinary housekeeping, and notifying on it would train the recipient to ignore the ones that
/// matter.
/// </para>
///
/// <para>
/// Counts and ids, never a name: the designations were not deleted and the agent will read them on
/// the holdings this points to.
/// </para>
/// </summary>
public sealed record HouseholdMemberRemovedEvent(
    Guid TenantId,
    Guid ClientId,
    Guid MemberId,
    int DesignationsToReview,
    IReadOnlyList<Guid> AffectedHoldingIds,
    Guid RemovedBy) : IntegrationEventBase;
