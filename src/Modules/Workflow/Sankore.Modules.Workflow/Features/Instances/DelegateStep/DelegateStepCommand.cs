using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.DelegateStep;

public sealed record DelegateStepCommand(
    Guid InstanceId,
    Guid StepId,
    Guid ToUserId,
    string? Comment = null
) : IRequest<Result>, ICommand;
