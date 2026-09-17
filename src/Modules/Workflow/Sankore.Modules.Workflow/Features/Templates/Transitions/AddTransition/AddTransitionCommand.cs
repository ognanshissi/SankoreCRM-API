using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Behaviors;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddTransition;

internal sealed record AddTransitionCommand(
    Guid TemplateId,
    Guid FromStateId,
    Guid? ToStateId,
    string EventCode,
    WorkflowStatus? ToTerminalStatus,
    string? ConditionJson,
    int Priority = 0
) : IRequest<Result<Guid>>, ICommand;
