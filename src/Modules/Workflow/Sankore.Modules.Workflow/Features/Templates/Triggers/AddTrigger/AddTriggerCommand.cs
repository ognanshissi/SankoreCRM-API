using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;

internal sealed record AddTriggerCommand(
    Guid TemplateId,
    TriggerType TriggerType,
    string EventName,
    string? ConditionJson = null
) : IRequest<Result<Guid>>, ICommand;
