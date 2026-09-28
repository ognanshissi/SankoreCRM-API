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
