using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CancelTask;

internal sealed record CancelTaskCommand(Guid InstanceId, Guid TaskId) : IRequest<Result>, ICommand;
