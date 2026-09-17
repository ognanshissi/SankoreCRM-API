using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CompleteTask;

internal static class CompleteTaskEndpoint
{
    internal static IEndpointRouteBuilder MapCompleteTask(this IEndpointRouteBuilder app)
    {
        app.MapPost("{instanceId:guid}/tasks/{taskId:guid}/complete",
            async (Guid instanceId, Guid taskId, CompleteTaskRequest? body, ISender sender, CancellationToken ct) =>
            {
                var cmd = new CompleteTaskCommand(instanceId, taskId, body?.Comment);
                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.Ok()
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("CompleteWorkflowTask")
        .WithSummary("Complete a workflow task and optionally advance the instance")
        .RequireAuthorization()
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

internal sealed record CompleteTaskRequest(string? Comment = null);
