using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.ListTasks;

internal sealed record ListTasksQuery(Guid InstanceId) : IRequest<Result<List<TaskDto>>>;
