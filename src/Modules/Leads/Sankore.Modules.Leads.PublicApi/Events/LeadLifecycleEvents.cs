namespace Sankore.Modules.Leads.PublicApi.Events;

using Sankore.Shared.Kernel;

// ─────────────────────────────────────────────────────────────────────────────
// The lead and project lifecycle, published by M13 through its outbox.
//
// These are NOT raised by handlers. `LeadsDbContext` translates the domain events the Lead and
// Opportunity aggregates already raise, at SaveChanges, into these records and writes them to
// the outbox in the same transaction (`LeadLifecycleEventTranslator`). A handler that changes a
// lead's status therefore cannot forget to announce it — before this, almost none did, and the
// domain events were raised and dropped.
//
// Same contract as Customers' events: TenantId first, identifiers and enum NAMES only, never a
// personal value (name, phone, e-mail, address) nor free text an agent typed (loss detail,
// closing motive). They are what outbound webhooks are built from.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A lead was captured — by an agent, an import, a web form, a webhook or a pull.</summary>
public sealed record LeadCreatedEvent(
    Guid TenantId,
    Guid LeadId,
    string Status,
    string Source,
    Guid? LeadSourceConfigId,
    Guid? OwnerId,
    Guid? AgencyId,
    Guid? CustomerId) : IntegrationEventBase;

/// <summary>
/// Somebody now holds the lead: an agent through dispatch (<paramref name="Method"/> =
/// <c>Dispatch</c>, with its assignment), or an owner set by hand or in bulk (<c>Owner</c>).
/// </summary>
public sealed record LeadAssignedEvent(
    Guid TenantId,
    Guid LeadId,
    Guid UserId,
    Guid? PreviousUserId,
    Guid? AssignmentId,
    string Method) : IntegrationEventBase;

/// <summary>Any change of status. The specific transitions below are announced as well.</summary>
public sealed record LeadStatusChangedEvent(
    Guid TenantId,
    Guid LeadId,
    string PreviousStatus,
    string Status) : IntegrationEventBase;

/// <summary>The lead moved along the contact stages (New → … → NeedIdentified).</summary>
public sealed record LeadStageChangedEvent(
    Guid TenantId,
    Guid LeadId,
    string PreviousStage,
    string Stage) : IntegrationEventBase;

public sealed record LeadQualifiedEvent(
    Guid TenantId,
    Guid LeadId,
    int Score) : IntegrationEventBase;

/// <summary>The lead became a client. <paramref name="ClientId"/> is null on the legacy path that converts without one.</summary>
public sealed record LeadConvertedToClientEvent(
    Guid TenantId,
    Guid LeadId,
    Guid? ClientId) : IntegrationEventBase;

/// <summary>Closed as Lost, Disqualified or Archived. The agent's motive is deliberately not carried.</summary>
public sealed record LeadClosedEvent(
    Guid TenantId,
    Guid LeadId,
    string Status) : IntegrationEventBase;

/// <summary>A closed lead was put back to work.</summary>
public sealed record LeadReopenedEvent(
    Guid TenantId,
    Guid LeadId,
    string PreviousStatus,
    string Status) : IntegrationEventBase;

/// <summary><paramref name="AbsorbedLeadId"/> was merged into <paramref name="SurvivorLeadId"/> and is archived.</summary>
public sealed record LeadMergedEvent(
    Guid TenantId,
    Guid AbsorbedLeadId,
    Guid SurvivorLeadId) : IntegrationEventBase;

/// <summary>A consent was withdrawn. <paramref name="ConsentType"/> is the type's name.</summary>
public sealed record LeadConsentWithdrawnEvent(
    Guid TenantId,
    Guid LeadId,
    string ConsentType) : IntegrationEventBase;

/// <summary>A project (an <c>Opportunity</c>) was opened on a lead or on a client.</summary>
public sealed record ProjectCreatedEvent(
    Guid TenantId,
    Guid ProjectId,
    Guid? LeadId,
    Guid? ClientId,
    string Stage,
    Guid? ProductLineId,
    decimal? Amount,
    string? Currency,
    Guid? OwnerId) : IntegrationEventBase;

/// <summary>The project moved to another step, or another phase, without closing.</summary>
public sealed record ProjectStageChangedEvent(
    Guid TenantId,
    Guid ProjectId,
    Guid? LeadId,
    Guid? ClientId,
    string PreviousStage,
    string Stage,
    Guid? PreviousStepId,
    Guid? StepId) : IntegrationEventBase;

/// <summary>The project was won or lost (<paramref name="Outcome"/> = <c>Won</c> | <c>Lost</c>).</summary>
public sealed record ProjectClosedEvent(
    Guid TenantId,
    Guid ProjectId,
    Guid? LeadId,
    Guid? ClientId,
    string Outcome,
    decimal? Amount,
    string? Currency) : IntegrationEventBase;
