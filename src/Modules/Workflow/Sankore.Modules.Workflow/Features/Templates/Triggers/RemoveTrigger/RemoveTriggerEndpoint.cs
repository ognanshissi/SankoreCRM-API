using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Triggers.RemoveTrigger;

internal static class RemoveTriggerEndpoint
{
    internal static IEndpointRouteBuilder MapRemoveTrigger(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{templateId:guid}/triggers/{triggerId:guid}", async (
            Guid templateId,
            Guid triggerId,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new RemoveTriggerCommand(templateId, triggerId), ct);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("RemoveTrigger")
        .WithSummary("Delete an event trigger from a workflow template.")
        .RequireAuthorization(Permissions.CanManageWorkflowTriggers.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces<object>(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
