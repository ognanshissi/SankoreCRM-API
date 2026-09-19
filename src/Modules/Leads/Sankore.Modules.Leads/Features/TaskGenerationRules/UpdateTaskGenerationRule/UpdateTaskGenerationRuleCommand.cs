namespace Sankore.Modules.Leads.Features.TaskGenerationRules.UpdateTaskGenerationRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateTaskGenerationRuleCommand(
    Guid RuleId,
    string TriggerEventType,
    CrmTaskType TaskType,
    CrmTaskPriority Priority,
    string TitleTemplate,
    TimeSpan SlaDuration,
    TimeSpan DueDuration,
    string? DescriptionTemplate = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskGenerationRule";
    public string? ResourceId  => RuleId.ToString();
}
