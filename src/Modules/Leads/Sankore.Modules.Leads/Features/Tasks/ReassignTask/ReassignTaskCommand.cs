namespace Sankore.Modules.Leads.Features.Tasks.ReassignTask;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Reassigns a CRM task to a different agent (US-M13-083).
/// ActorId == null → SYSTEM-initiated (e.g. triggered by SLA breach).
/// ActorId != null → manager-initiated; checked against PermissionAttribution at the endpoint.
/// SlaExtensionHours → when provided the deadline is recalculated to UtcNow + hours.
/// </summary>
public sealed record ReassignTaskCommand(
    Guid TaskId,
    Guid NewAgentId,
    string Reason,
    Guid? ActorId = null,
    int? SlaExtensionHours = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}
