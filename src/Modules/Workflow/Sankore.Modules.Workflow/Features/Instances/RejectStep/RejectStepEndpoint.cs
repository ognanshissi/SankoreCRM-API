using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.RejectStep;

internal static class RejectStepEndpoint
{
    public static IEndpointRouteBuilder MapRejectStep(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/reject", Handle)
            .WithName("RejectWorkflowStep")
            .WithSummary("Reject the current step of a workflow instance")
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
        Guid id, RejectStepRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RejectStepCommand(id, req.Comment), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record RejectStepRequest(string? Comment);
