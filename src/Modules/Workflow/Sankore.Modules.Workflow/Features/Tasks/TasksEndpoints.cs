using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Tasks.CancelTask;
using Sankore.Modules.Workflow.Features.Tasks.CompleteTask;
using Sankore.Modules.Workflow.Features.Tasks.GetTask;
using Sankore.Modules.Workflow.Features.Tasks.ListTasks;

namespace Sankore.Modules.Workflow.Features.Tasks;

public static class TasksEndpoints
{
    public static IEndpointRouteBuilder MapTasksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("instances")
            .WithTags("WorkflowTasks");

        return group
            .MapListTasks()
            .MapGetTask()
            .MapCompleteTask()
            .MapCancelTask();
    }
}
