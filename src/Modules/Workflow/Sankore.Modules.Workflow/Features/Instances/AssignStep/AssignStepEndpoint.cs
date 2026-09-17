using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.AssignStep;

internal static class AssignStepEndpoint
{
    internal static IEndpointRouteBuilder MapAssignStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{instanceId:guid}/steps/{stepId:guid}/assign", async (
            Guid instanceId,
            Guid stepId,
            AssignRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(
                new AssignStepCommand(instanceId, stepId, body.AssignedToUserId), ct);

            return result.IsSuccess
                ? Results.NoContent()
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("AssignStep")
        .WithSummary("Assign an active workflow step to a specific user.")
        .RequireAuthorization(Permissions.CanAssignWorkflowStep.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private sealed record AssignRequest(Guid AssignedToUserId);
}
