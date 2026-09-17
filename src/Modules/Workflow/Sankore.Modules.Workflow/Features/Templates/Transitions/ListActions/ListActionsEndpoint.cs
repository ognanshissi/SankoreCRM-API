using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.ListActions;

internal static class ListActionsEndpoint
{
    internal static IEndpointRouteBuilder MapListActions(this IEndpointRouteBuilder app)
    {
        app.MapGet("{templateId:guid}/transitions/{transitionId:guid}/actions",
            async (Guid templateId, Guid transitionId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListActionsQuery(templateId, transitionId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.NotFound(new { error = result.Error });
            })
        .WithName("ListWorkflowActions")
        .WithSummary("List all actions for a workflow transition")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces<List<ActionDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}

internal sealed record ActionDto(
    Guid Id,
    ActionType ActionType,
    int ExecutionOrder,
    string ConfigJson);
