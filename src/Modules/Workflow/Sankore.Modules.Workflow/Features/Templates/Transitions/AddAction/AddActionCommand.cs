using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Behaviors;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;

internal sealed record AddActionCommand(
    Guid TemplateId,
    Guid TransitionId,
    ActionType ActionType,
    string ConfigJson,
    int ExecutionOrder = 0
) : IRequest<Result<Guid>>, ICommand;
