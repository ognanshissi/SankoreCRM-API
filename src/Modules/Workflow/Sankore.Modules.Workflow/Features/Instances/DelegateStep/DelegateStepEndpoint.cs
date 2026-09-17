using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sankore.Modules.Workflow.Features.Instances.DelegateStep;

internal static class DelegateStepEndpoint
{
    internal static IEndpointRouteBuilder MapDelegateStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{instanceId:guid}/steps/{stepId:guid}/delegate", async (
            Guid instanceId,
            Guid stepId,
            DelegateRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(
                new DelegateStepCommand(instanceId, stepId, body.ToUserId, body.Comment), ct);

            return result.IsSuccess
                ? Results.NoContent()
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("DelegateStep")
        .WithSummary("Delegate the current step to another user (current assignee only).")
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent)
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private sealed record DelegateRequest(Guid ToUserId, string? Comment = null);
}
