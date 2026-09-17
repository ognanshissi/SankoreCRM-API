using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.RemoveTransition;

internal static class RemoveTransitionEndpoint
{
    internal static IEndpointRouteBuilder MapRemoveTransition(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{templateId:guid}/transitions/{transitionId:guid}",
            async (Guid templateId, Guid transitionId, ISender sender, CancellationToken ct) =>
            {
                var cmd = new RemoveTransitionCommand(templateId, transitionId);
                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("RemoveWorkflowTransition")
        .WithSummary("Remove a custom transition from a workflow template")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}
