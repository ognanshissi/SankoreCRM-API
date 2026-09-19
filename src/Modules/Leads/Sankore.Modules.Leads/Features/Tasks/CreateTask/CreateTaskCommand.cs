namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateTaskCommand(
    Guid TenantId,
    CrmTaskType Type,
    CrmTaskPriority Priority,
    string Title,
    DateTimeOffset DueAt,
    Guid? LeadId = null,
    Guid? AssignedAgentId = null,
    DateTimeOffset? SlaDeadline = null,
    string? Description = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => LeadId?.ToString();
}
