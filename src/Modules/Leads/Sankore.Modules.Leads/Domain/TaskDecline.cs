namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Immutable audit record for every task decline (US-M13-084).
/// Captures which agent declined, when, and why — feeds Analytics
/// for decline-rate tracking and manager dashboards.
/// </summary>
public sealed class TaskDecline : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid AgentId { get; private set; }
    public string Reason { get; private set; } = default!;
    public DateTimeOffset DeclinedAt { get; private set; }

    private TaskDecline() { }

    public static TaskDecline Create(
        Guid tenantId,
        Guid taskId,
        Guid agentId,
        string reason)
        => new()
        {
            Id        = Guid.NewGuid(),
            TenantId  = tenantId,
            TaskId    = taskId,
            AgentId   = agentId,
            Reason    = reason,
            DeclinedAt = DateTimeOffset.UtcNow
        };
}
