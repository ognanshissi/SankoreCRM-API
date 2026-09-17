using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateStep;

internal static class UpdateStepEndpoint
{
    public static IEndpointRouteBuilder MapUpdateStep(this IEndpointRouteBuilder app)
    {
        app.MapPut("{id:guid}/steps/{stepId:guid}", Handle)
            .WithName("UpdateWorkflowStep")
            .WithSummary("Update the name, description, approver role, or SLA timeout of a step on a draft template.")
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
        Guid id, Guid stepId, UpdateStepRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new UpdateStepCommand(id, stepId, req.Name, req.Description,
                                  req.ApproverRoleCode, req.TimeoutHours), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

internal sealed record UpdateStepRequest(
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours);
