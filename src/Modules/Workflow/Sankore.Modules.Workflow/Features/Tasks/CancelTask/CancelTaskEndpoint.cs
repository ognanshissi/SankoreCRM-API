using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CancelTask;

internal static class CancelTaskEndpoint
{
    internal static IEndpointRouteBuilder MapCancelTask(this IEndpointRouteBuilder app)
    {
        app.MapPost("{instanceId:guid}/tasks/{taskId:guid}/cancel",
            async (Guid instanceId, Guid taskId, ISender sender, CancellationToken ct) =>
            {
                var cmd = new CancelTaskCommand(instanceId, taskId);
                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.Ok()
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("CancelWorkflowTask")
        .WithSummary("Cancel a pending workflow task")
        .RequireAuthorization()
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}
