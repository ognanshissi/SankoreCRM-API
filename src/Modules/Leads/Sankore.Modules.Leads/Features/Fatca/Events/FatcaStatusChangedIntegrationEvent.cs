namespace Sankore.Modules.Leads.Features.Fatca.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a person's FATCA status changes (BNF-B-11). Consumed by M08 for the compliance
/// notification and by M01 once the lead becomes a client.
///
/// <para>
/// <b>The STATUS and the subject, never the content.</b> No TIN, no document reference, no list of
/// declared indices: the status is what a consumer acts on, and an outbox row plus every
/// subscriber's logs is the wrong place for a US taxpayer identification number.
/// </para>
/// </summary>
/// <param name="RequiresComplianceReview">
/// True for <c>UsPerson</c> and <c>Recalcitrant</c> — the two BNF-B-11 says must be flagged to the
/// compliance officer. Carried as a flag so the consumer does not have to know which statuses those
/// are, which is a rule that belongs to this module.
/// </param>
public sealed record FatcaStatusChangedIntegrationEvent(
    Guid TenantId,
    Guid LeadId,
    string SubjectType,
    Guid SubjectId,
    string Status,
    string? PreviousStatus,
    bool RequiresComplianceReview) : IntegrationEventBase;
