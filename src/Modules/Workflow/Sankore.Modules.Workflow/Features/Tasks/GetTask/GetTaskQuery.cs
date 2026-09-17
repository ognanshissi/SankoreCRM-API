using MediatR;
using Sankore.Modules.Workflow.Features.Tasks.ListTasks;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.GetTask;

internal sealed record GetTaskQuery(Guid InstanceId, Guid TaskId) : IRequest<Result<TaskDto>>;
