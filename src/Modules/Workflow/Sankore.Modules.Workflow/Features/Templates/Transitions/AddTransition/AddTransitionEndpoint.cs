using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Transitions.AddTransition;

internal static class AddTransitionEndpoint
{
    internal static IEndpointRouteBuilder MapAddTransition(this IEndpointRouteBuilder app)
    {
        app.MapPost("{templateId:guid}/transitions",
            async (Guid templateId, AddTransitionRequest body, ISender sender, CancellationToken ct) =>
            {
                var cmd = new AddTransitionCommand(
                    templateId,
                    body.FromStateId,
                    body.ToStateId,
                    body.EventCode,
                    body.ToTerminalStatus,
                    body.ConditionJson,
                    body.Priority);

                var result = await sender.Send(cmd, ct);
                return result.IsSuccess
                    ? Results.Ok(new { transitionId = result.Value })
                    : Results.BadRequest(new { error = result.Error });
            })
        .WithName("AddWorkflowTransition")
        .WithSummary("Add a custom conditional transition to a workflow template")
        .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .WithOpenApi();

        return app;
    }
}

internal sealed record AddTransitionRequest(
    Guid FromStateId,
    Guid? ToStateId,
    string EventCode,
    WorkflowStatus? ToTerminalStatus = null,
    string? ConditionJson = null,
    int Priority = 0);
