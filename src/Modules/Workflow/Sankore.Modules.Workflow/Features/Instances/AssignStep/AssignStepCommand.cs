using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.AssignStep;

public sealed record AssignStepCommand(
    Guid InstanceId,
    Guid StepId,
    Guid AssignedToUserId
) : IRequest<Result>, ICommand;
