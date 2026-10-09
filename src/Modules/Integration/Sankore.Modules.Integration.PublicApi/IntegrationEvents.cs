namespace Sankore.Modules.Integration.PublicApi;

using Sankore.Shared.Kernel;

// Contract rules, identical to every other module's event file:
//   * `: IntegrationEventBase` — EventId + OccurredAt come for free.
//   * `Guid TenantId` is ALWAYS the first field: a consumer runs outside any HTTP context and
//     reads it to build its own tenant predicate.
//   * `CrmCustomerId` is an OPAQUE reference to the customer record of M01. This module never
//     assumes it is a Customers row id, and M01 exposes no physical FK.
//   * Enum-typed concepts travel as strings so a consumer never has to reference this module's
//     domain assembly.
//   * Delivery is at-least-once (outbox + broker): every consumer is idempotent.

/// <summary>
/// The customer now exists in the external system. Chains the onboarding: the consumer requests
/// the KYC level, then the account if a product was chosen (INT-14).
/// </summary>
public sealed record CbsCustomerCreatedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    Guid ConnectionId,
    string ExternalCustomerId,
    DateTimeOffset CreatedAt) : IntegrationEventBase;

/// <summary>The account is open. Carries both references so a screen can show them at once.</summary>
public sealed record CbsAccountOpenedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    Guid ConnectionId,
    string ExternalCustomerId,
    string ExternalAccountId,
    string ProductCode,
    DateTimeOffset OpenedAt) : IntegrationEventBase;

/// <summary>
/// A command will not be delivered. Consumed by M08 to alert the administrator — and that is the
/// whole reason a rejection is an event rather than a log line: nobody reads the rejection queue
/// unprompted.
///
/// <para>
/// <paramref name="ErrorFamily"/> travels so the notification can say whether the IMF has
/// something to fix (<c>Technical</c>) or the external system refused on the merits
/// (<c>Functional</c>).
/// </para>
/// </summary>
public sealed record IntegrationCommandRejectedEvent(
    Guid TenantId,
    Guid CommandId,
    Guid ConnectionId,
    string CommandType,
    string EntityType,
    Guid CrmId,
    string ErrorFamily,
    string ErrorCode,
    string? Detail,
    int Attempts,
    DateTimeOffset RejectedAt) : IntegrationEventBase;

/// <summary>
/// The CBS and the CRM disagree about a customer's KYC tier (INT-21). Not corrected
/// automatically: which side is right is a compliance decision, so it is reported.
/// </summary>
public sealed record CbsKycMismatchDetectedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    string CrmLevel,
    string CbsLevel,
    DateTimeOffset DetectedAt) : IntegrationEventBase;

/// <summary>
/// A simplified-KYC customer is nearing a ceiling (INT-22). Published once per customer and per
/// month: an alert repeated daily is an alert nobody reads.
/// </summary>
public sealed record KycLimitApproachingEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    string LimitKind,
    decimal Observed,
    decimal Ceiling,
    int ThresholdPercent,
    DateTimeOffset DetectedAt) : IntegrationEventBase;

/// <summary>A ceiling is exceeded. M02 raises an upgrade task, M08 notifies (INT-22).</summary>
public sealed record KycLimitExceededEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    string LimitKind,
    decimal Observed,
    decimal Ceiling,
    DateTimeOffset DetectedAt) : IntegrationEventBase;

/// <summary>
/// An outbound batch file was deposited and has gone unacknowledged beyond the connection's
/// tolerance (INT-25 criterion 3).
///
/// <para>
/// An event and not a log line, for the reason the whole rejection queue exists: nobody reads the
/// batch table unprompted. Until an acknowledgement arrives, every command in the file is still
/// <c>Batched</c> — owed, not failed — so this is the only thing that will tell an administrator
/// that a cycle has silently stopped turning.
/// </para>
///
/// <para>
/// Distinct from <see cref="IntegrationCommandRejectedEvent"/>, which says the external system
/// refused a write. Here nothing has been refused: the file may have been mis-deposited, the
/// partner's import may not have run, or its answer may have gone to the wrong directory. The
/// commands remain closable by a late acknowledgement, and <paramref name="PendingCommandCount"/>
/// is what tells the operator how much is waiting on it.
/// </para>
/// </summary>
public sealed record IntegrationBatchAckOverdueEvent(
    Guid TenantId,
    Guid ConnectionId,
    Guid BatchFileId,
    string FileName,
    long SequenceNo,
    DateTimeOffset SentAt,
    int AckTimeoutHours,
    int PendingCommandCount,
    DateTimeOffset DetectedAt) : IntegrationEventBase;

/// <summary>The insurer issued the policy (ASS-04).</summary>
public sealed record PolicyIssuedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    Guid ConnectionId,
    Guid CrmProductId,
    string ExternalPolicyId,
    string PolicyNumber,
    decimal PremiumAmount,
    string Currency,
    DateOnly EffectiveDate,
    DateTimeOffset IssuedAt) : IntegrationEventBase;

/// <summary>A policy changed state at the insurer's end (ASS-07).</summary>
public sealed record PolicyStatusChangedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    string ExternalPolicyId,
    string PreviousStatus,
    string CurrentStatus,
    string? Reason,
    DateTimeOffset ChangedAt) : IntegrationEventBase;

/// <summary>A claim changed state (ASS-09).</summary>
public sealed record ClaimStatusChangedEvent(
    Guid TenantId,
    Guid CrmCustomerId,
    string ExternalClaimId,
    string PreviousStatus,
    string CurrentStatus,
    string? MissingDocuments,
    DateTimeOffset ChangedAt) : IntegrationEventBase;

/// <summary>
/// New reconciliation gaps were recorded today (INT-34). The summary, not the gaps: the counts
/// per type are what an administrator acts on, and the detail stays behind the permission.
/// </summary>
public sealed record ReconciliationCompletedEvent(
    Guid TenantId,
    Guid ConnectionId,
    Guid RunId,
    int CheckedCount,
    int NewGapCount,
    int ClosedGapCount,
    IReadOnlyDictionary<string, int> GapsByType,
    DateTimeOffset FinishedAt) : IntegrationEventBase;
