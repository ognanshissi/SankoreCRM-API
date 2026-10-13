namespace Sankore.Modules.Kyc.PublicApi;

using Sankore.Shared.Kernel;

// ─────────────────────────────────────────────────────────────────────────────
// Integration events published BY the KYC module (M02) and consumed by the
// modules that own the customer record (today: M01 Customers).
//
// Contract rules, identical to every other module's event file:
//   * `: IntegrationEventBase` — EventId + OccurredAt come for free.
//   * `Guid TenantId` is ALWAYS the first field: a consumer runs outside any
//     HTTP context and reads it to build its own tenant predicate.
//   * `CustomerEntityId` is an OPAQUE reference to the customer record. M02 must
//     never assume it is a Customers row id, and M01 never exposes a physical FK.
//   * Enum-typed concepts travel as strings so a consumer never has to reference
//     another module's domain assembly.
//   * Delivery is at-least-once (outbox + broker): every consumer is idempotent.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The KYC file of a customer was approved. The owning module moves the record
/// out of its "pending KYC" state — see the M01 consumer for the exact rule
/// (a suspended or archived client only has its KYC status refreshed).
/// </summary>
public sealed record KycValidatedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    DateTimeOffset ValidatedAt) : IntegrationEventBase;

/// <summary>
/// The KYC file was rejected. <paramref name="Reason"/> is an analyst-entered
/// motive shown to operators — never a sensitive value (no document number,
/// no phone, no address).
/// </summary>
public sealed record KycRejectedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    string Reason,
    DateTimeOffset RejectedAt) : IntegrationEventBase;

/// <summary>
/// The AML risk rating of the customer changed. <paramref name="RiskLevel"/> is
/// the NAME of the level ("Unknown", "Low", "Medium", "High"); a consumer that
/// cannot parse it must log and ignore rather than guess. A risk change never
/// changes the customer's lifecycle status.
/// </summary>
public sealed record KycRiskLevelChangedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    string RiskLevel,
    DateTimeOffset ChangedAt) : IntegrationEventBase;

/// <summary>
/// A KYC file was opened for a customer. Consumed by M08 to tell the enrolling agent there is a
/// file to complete.
///
/// <paramref name="Channel"/> is the NAME of the enrolment channel ("Agency", "LeadConversion",
/// …) so no consumer has to reference this module's domain assembly.
/// </summary>
/// <summary>
/// A customer sent their own KYC file from their own device, through a single-use link
/// (KRM-B-07).
///
/// <para>
/// It exists to trigger the ROUTING of KRM-B-08 — automatic approval or a human — and that is why
/// it is published at submission rather than inferred later from the channel: the decision needs
/// the product line and the responsible agent in one place, and a consumer cannot correlate a
/// status change with a file read that may already have moved on.
/// </para>
///
/// <para>
/// Carries no identity and no evidence: an id for the customer, an id for the file, and the two
/// references the routing needs. A rendered notification downstream reads the file.
/// </para>
/// </summary>
/// <param name="ProductLineId">
/// The catalogue line whose policy decides how this file is validated. Null for a file that names
/// no product, which cannot be submitted remotely — carried nullable so the contract matches the
/// file rather than promising something the file cannot guarantee.
/// </param>
/// <param name="ResponsibleAgentId">
/// Who gets told when the file needs a human (KRM-B-09). Null for a file with no agent — a counter
/// enrolment, or one predating KRM-B-02.
/// </param>
public sealed record KycRemoteSubmittedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid KycFileId,
    Guid InvitationId,
    Guid? ProductLineId,
    Guid? ResponsibleAgentId,
    DateTimeOffset SubmittedAt) : IntegrationEventBase;

/// <summary>
/// A remotely collected KYC file has been ROUTED (KRM-B-08): either approved with no human, or
/// handed to one with a coded reason.
///
/// <para>
/// <b>Why it exists beside <c>KycValidatedEvent</c>.</b> That one means "this customer's KYC is
/// approved" and is what M01 activates a client on; it says nothing about how, and must not — a
/// second definition of "validated" is how two modules end up disagreeing about whether somebody
/// is a customer. This one says who decided, which is what three unrelated consumers need and
/// none of them can infer: the agent's notification (KRM-B-09), the customer's email (KRM-B-10),
/// and the after-the-fact sampling of unattended approvals (KRM-B-11).
/// </para>
///
/// <para>
/// It is published on BOTH outcomes rather than only on the manual one, so the sampling has a
/// single trigger and does not have to watch an approval event and filter it by a column.
/// </para>
/// </summary>
/// <param name="Automatic">
/// True when the product's policy approved the file and nobody was asked. The file is already at
/// its <paramref name="Tier"/> when this is published.
/// </param>
/// <param name="RoutingReason">
/// One of <c>KycRoutingReasons</c> when a human was asked; null when
/// <paramref name="Automatic"/> is true. A CODE — a consumer that shows it to somebody translates
/// it, and no consumer may branch on a substring of it.
/// </param>
/// <param name="Tier">
/// The file's tier at the moment of routing: the granted one on an automatic approval,
/// <c>None</c> on a manual route (the human has not decided yet).
/// </param>
/// <param name="ResponsibleAgentId">
/// The agent accountable for the file, carried so a notification consumer does not have to read
/// M02's schema to find out who to tell. Null on a file opened without one — the consumer then
/// falls back to the agency, which is KRM-B-09's own cascade.
/// </param>
public sealed record KycRemoteDecidedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid KycFileId,
    bool Automatic,
    string? RoutingReason,
    string Tier,
    Guid? ProductLineId,
    Guid? ResponsibleAgentId,
    DateTimeOffset DecidedAt) : IntegrationEventBase;

public sealed record KycInitiatedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid KycFileId,
    string Channel,
    Guid InitiatedBy) : IntegrationEventBase;

/// <summary>
/// A biometric verification ran on a KYC file and reached a business outcome. Consumed by M08 to
/// tell the enrolling agent what to do next, and by anything tracking enrolment throughput.
///
/// <para>
/// It is published for the two outcomes a verification can actually REACH, named by
/// <paramref name="Outcome"/>:
/// <list type="bullet">
/// <item><c>"SCORED"</c> — the service answered and the file carries a score. Both
///   <paramref name="ConfidenceScore"/> and <paramref name="ConfidenceLevel"/> are set.</item>
/// <item><c>"CAPTURE_REJECTED"</c> — the service worked and says the photo is unusable. There is
///   no score; <paramref name="RejectionCode"/> names the defect and the agent is asked for a
///   better capture.</item>
/// </list>
/// A consumer that does not recognise the outcome must log and ignore rather than guess.
/// </para>
///
/// <para>
/// Nothing is published when the biometric service was UNREACHABLE. An outage is not a fact about
/// the customer, the file stays in verification and the attempt is replayed — announcing it would
/// put our own downtime in a customer's compliance history.
/// </para>
///
/// <para>
/// <paramref name="Status"/> is the NAME of the resulting internal status ("Validating",
/// "ComplementRequired"); <paramref name="RejectionCode"/> is a stable technical code, never a
/// sentence and never a field value. No document number, no OCR reading and no image reference
/// travels on this event — it crosses the module boundary and a consumer has no business holding
/// any of that.
/// </para>
/// </summary>
public sealed record KycVerificationCompletedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid KycFileId,
    string Outcome,
    string Status,
    int? ConfidenceScore,
    string? ConfidenceLevel,
    string? RejectionCode,
    DateTimeOffset CompletedAt) : IntegrationEventBase;

/// <summary>
/// The customer moved between the simplified and the full tier. Consumed by the modules that cap
/// operations (M03 savings, M07 mobile money) to drop their cached ceilings — a stale ceiling
/// after a downgrade is an operation that should have been refused.
/// </summary>
public sealed record KycTierChangedEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    string PreviousTier,
    string CurrentTier,
    DateTimeOffset ChangedAt) : IntegrationEventBase;

/// <summary>
/// A periodic or event-driven review has come due. Consumed by M08 to tell the agency.
/// <paramref name="Trigger"/> is the NAME of the trigger ("Periodic", "Event").
/// </summary>
public sealed record KycReviewDueEvent(
    Guid TenantId,
    Guid CustomerEntityId,
    Guid KycFileId,
    DateOnly DueDate,
    string Trigger,
    DateTimeOffset RaisedAt) : IntegrationEventBase;

