using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.ListTriggers;

internal sealed record ListTriggersQuery(Guid TemplateId) : IRequest<Result<IReadOnlyList<TriggerDto>>>;
