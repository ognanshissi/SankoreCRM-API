namespace Sankore.Modules.Leads.Features.LeadSources.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published via outbox when a lead source is created or updated (US-F13.37-BE-06).
/// </summary>
public sealed record LeadSourceChangedEvent(
    Guid SourceId,
    Guid TenantId,
    string Code,
    string ChangeType,
    IReadOnlyList<string> ChangedFields) : IntegrationEventBase;
