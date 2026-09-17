using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;

internal static class AddActionEndpoint
{
    internal static IEndpointRouteBuilder MapAddAction(this IEndpointRouteBuilder app)
    {
        app.MapPost("{templateId:guid}/transitions/{transitionId:guid}/actions",
            async (Guid templateId, Guid transitionId, AddActionRequest body, ISender sender, CancellationToken ct) =>
            {
                var cmd = new AddActionCommand(
                    templateId,
                    transitionId,
                    body.ActionType,
                    body.ConfigJson,
                    body.ExecutionOrder);

                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.Ok(new { actionId = result.Value })
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("AddWorkflowAction")
        .WithSummary("Add a side-effect action to a workflow transition")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

internal sealed record AddActionRequest(
    ActionType ActionType,
    string ConfigJson = "{}",
    int ExecutionOrder = 0);
