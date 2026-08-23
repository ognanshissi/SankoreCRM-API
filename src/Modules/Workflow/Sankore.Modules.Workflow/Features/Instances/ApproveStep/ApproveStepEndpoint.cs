using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ApproveStep;

internal static class ApproveStepEndpoint
{
    public static IEndpointRouteBuilder MapApproveStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/approve", Handle)
            .WithName("ApproveWorkflowStep")
            .WithSummary("Approve the current step of a workflow instance")
            .RequireAuthorization(Permissions.CanApproveWorkflow.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ApproveStepRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ApproveStepCommand(id, req.Comment), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record ApproveStepRequest(string? Comment);
