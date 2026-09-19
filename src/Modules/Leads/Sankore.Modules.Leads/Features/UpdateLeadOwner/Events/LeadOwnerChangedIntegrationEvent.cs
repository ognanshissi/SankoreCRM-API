namespace Sankore.Modules.Leads.Features.UpdateLeadOwner.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead's commercial owner changes.
/// Consumed by: Notifications module (alert new owner), Analytics (ownership metrics).
/// </summary>
public sealed record LeadOwnerChangedIntegrationEvent(
    Guid LeadId,
    Guid TenantId,
    Guid? PreviousOwnerId,
    Guid NewOwnerId,
    string AssignmentMethod,
    string? Reason,
    Guid AssignedBy,
    DateTimeOffset AssignedAt) : IntegrationEventBase;
