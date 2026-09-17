using MediatR;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Behaviors;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveTransition;

internal sealed record RemoveTransitionCommand(
    Guid TemplateId,
    Guid TransitionId
) : IRequest<Result>, ICommand;
