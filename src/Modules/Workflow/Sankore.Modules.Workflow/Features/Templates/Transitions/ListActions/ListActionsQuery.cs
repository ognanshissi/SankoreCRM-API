using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.ListActions;

internal sealed record ListActionsQuery(
    Guid TemplateId,
    Guid TransitionId
) : IRequest<Result<List<ActionDto>>>;
