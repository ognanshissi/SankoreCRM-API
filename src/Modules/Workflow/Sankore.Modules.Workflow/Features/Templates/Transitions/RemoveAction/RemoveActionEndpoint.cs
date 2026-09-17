using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveAction;

internal static class RemoveActionEndpoint
{
    internal static IEndpointRouteBuilder MapRemoveAction(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{templateId:guid}/transitions/{transitionId:guid}/actions/{actionId:guid}",
            async (Guid templateId, Guid transitionId, Guid actionId, ISender sender, CancellationToken ct) =>
            {
                var cmd = new RemoveActionCommand(templateId, transitionId, actionId);
                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("RemoveWorkflowAction")
        .WithSummary("Remove a side-effect action from a workflow transition")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}
