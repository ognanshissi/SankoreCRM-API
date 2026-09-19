namespace Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateTaskGenerationRuleCommand(
    Guid TenantId,
    string TriggerEventType,
    CrmTaskType TaskType,
    CrmTaskPriority Priority,
    string TitleTemplate,
    TimeSpan SlaDuration,
    TimeSpan DueDuration,
    string? DescriptionTemplate = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskGenerationRule";
    public string? ResourceId  => null;
}
