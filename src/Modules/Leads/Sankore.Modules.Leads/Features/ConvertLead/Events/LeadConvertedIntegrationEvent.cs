namespace Sankore.Modules.Leads.Features.ConvertLead.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published via the outbox when a lead is successfully converted.
/// Consumed by the Customers module to create the customer record,
/// and by Analytics/Notifications for conversion tracking.
/// </summary>
public sealed record LeadConvertedIntegrationEvent(
    Guid LeadId,
    Guid CustomerId,
    Guid TenantId,
    string FullName,
    string PhoneNumber,
    string? Email,
    DateTimeOffset ConvertedAt) : IntegrationEventBase;
