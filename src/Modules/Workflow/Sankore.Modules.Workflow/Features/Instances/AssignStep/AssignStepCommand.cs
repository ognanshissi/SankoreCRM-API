using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.AssignStep;

/// <param name="Reason">Why it changes hands — written in the history and told to the new holder.</param>
public sealed record AssignStepCommand(
    Guid InstanceId,
    Guid StepId,
    Guid AssignedToUserId,
    string? Reason = null
) : IRequest<Result>, ICommand;
