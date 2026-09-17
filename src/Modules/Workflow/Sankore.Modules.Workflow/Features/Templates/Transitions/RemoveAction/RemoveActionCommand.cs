using MediatR;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Behaviors;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveAction;

internal sealed record RemoveActionCommand(
    Guid TemplateId,
    Guid TransitionId,
    Guid ActionId
) : IRequest<Result>, ICommand;
