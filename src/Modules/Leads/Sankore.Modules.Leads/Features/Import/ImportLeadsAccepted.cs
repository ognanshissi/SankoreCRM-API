namespace Sankore.Modules.Leads.Features.Import;

/// <summary>202 payload for every import trigger, whatever the source.</summary>
public sealed record ImportLeadsAccepted(Guid ImportJobId);
