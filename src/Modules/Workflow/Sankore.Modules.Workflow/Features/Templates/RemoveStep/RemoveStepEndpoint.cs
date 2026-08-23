using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.RemoveStep;

internal static class RemoveStepEndpoint
{
    public static IEndpointRouteBuilder MapRemoveStep(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{id:guid}/steps/{stepId:guid}", Handle)
            .WithName("RemoveWorkflowStep")
            .WithSummary("Remove a step from a (draft) workflow template")
            .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, Guid stepId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RemoveStepCommand(id, stepId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
