using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.AddTrigger;

internal static class AddTriggerEndpoint
{
    internal static IEndpointRouteBuilder MapAddTrigger(this IEndpointRouteBuilder app)
    {
        app.MapPost("{templateId:guid}/triggers", async (
            Guid templateId,
            AddTriggerRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            var cmd = new AddTriggerCommand(
                templateId,
                body.TriggerType,
                body.EventName,
                body.ConditionJson);

            var result = await sender.Send(cmd, ct);
            return result.IsSuccess
                ? Results.Created($"/triggers/{result.Value}", new { id = result.Value })
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("AddTrigger")
        .WithSummary("Register an event trigger on a workflow template.")
        .RequireAuthorization(Permissions.CanManageWorkflowTriggers.Code)
        .Produces<object>(StatusCodes.Status201Created)
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private sealed record AddTriggerRequest(
        TriggerType TriggerType,
        string EventName,
        string? ConditionJson = null);
}
