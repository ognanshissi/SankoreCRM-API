namespace Sankore.Modules.Leads.PublicApi.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// A lead's personal data was erased on a person's request (data-purge lot 2). Consumers holding a
/// copy of it — calls and their recordings — must purge theirs, as for <c>ClientAnonymizedEvent</c>.
/// </summary>
public sealed record LeadErasedEvent(Guid TenantId, Guid LeadId, DateTimeOffset ErasedAt) : IntegrationEventBase;
