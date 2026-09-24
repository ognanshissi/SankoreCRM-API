namespace Sankore.Modules.Leads.Features.LeadSources.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a lead source's public key is rotated (US-F13.37-BE-09).
/// Hints only — never the actual key value.
/// </summary>
public sealed record LeadSourcePublicKeyRotatedEvent(
    Guid SourceId,
    Guid TenantId,
    string? OldKeyHint,
    string NewKeyHint) : IntegrationEventBase;
