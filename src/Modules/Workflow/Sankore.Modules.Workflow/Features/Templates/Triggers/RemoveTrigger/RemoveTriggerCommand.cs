using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.RemoveTrigger;

internal sealed record RemoveTriggerCommand(
    Guid TemplateId,
    Guid TriggerId
) : IRequest<Result>, ICommand;
