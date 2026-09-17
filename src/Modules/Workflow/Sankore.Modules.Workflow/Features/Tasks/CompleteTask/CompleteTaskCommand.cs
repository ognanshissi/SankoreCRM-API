using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CompleteTask;

internal sealed record CompleteTaskCommand(
    Guid InstanceId,
    Guid TaskId,
    string? Comment
) : IRequest<Result>, ICommand;
