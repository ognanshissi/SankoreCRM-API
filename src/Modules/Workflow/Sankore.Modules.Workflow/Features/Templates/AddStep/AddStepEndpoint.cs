using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

internal static class AddStepEndpoint
{
    public static IEndpointRouteBuilder MapAddStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/steps", Handle)
            .WithName("AddWorkflowStep")
            .WithSummary("Add a step to a (draft) workflow template")
            .RequireAuthorization(Permissions.CanManageWorkflowSteps.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, AddStepRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new AddStepCommand(id, req.Order, req.Name, req.Description,
                               req.ApproverRoleCode, req.TimeoutHours), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/workflow/templates/{id}/steps/{result.Value}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record AddStepRequest(
    int Order,
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours);
