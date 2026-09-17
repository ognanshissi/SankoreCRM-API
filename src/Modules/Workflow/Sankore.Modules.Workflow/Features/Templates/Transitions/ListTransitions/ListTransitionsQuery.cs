using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.ListTransitions;

internal sealed record ListTransitionsQuery(Guid TemplateId) : IRequest<Result<List<TransitionDto>>>;
