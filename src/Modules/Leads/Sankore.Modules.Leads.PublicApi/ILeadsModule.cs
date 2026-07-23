namespace Sankore.Modules.Leads.PublicApi;

/// <summary>
/// Contract other modules may use to read minimal lead information.
/// Kept intentionally small today (MVP scope); grows only when a concrete
/// cross-module need appears (e.g. Customers module querying original lead
/// source after conversion for attribution reporting).
/// </summary>
public interface ILeadsModule
{
    Task<LeadSummary?> GetLeadAsync(Guid leadId, CancellationToken ct);
}

public sealed record LeadSummary(
    Guid Id,
    string FullName,
    string Status,
    string Source,
    Guid? AssignedAgentId);
