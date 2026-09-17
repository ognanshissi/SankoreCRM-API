using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Tasks.ListTasks;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.GetTask;

internal static class GetTaskEndpoint
{
    internal static IEndpointRouteBuilder MapGetTask(this IEndpointRouteBuilder app)
    {
        app.MapGet("{instanceId:guid}/tasks/{taskId:guid}",
            async (Guid instanceId, Guid taskId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetTaskQuery(instanceId, taskId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.NotFound(new { error = result.Error });
            })
        .WithName("GetWorkflowTask")
        .WithSummary("Get a specific task for a workflow instance")
        .RequireAuthorization()
        .Produces<TaskDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}
