using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.CancelInstance;

internal static class CancelInstanceEndpoint
{
    public static IEndpointRouteBuilder MapCancelInstance(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/cancel", Handle)
            .WithName("CancelWorkflowInstance")
            .WithSummary("Cancel an in-progress workflow instance")
            .RequireAuthorization(Permissions.CanCancelWorkflow.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CancelInstanceCommand(id), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
