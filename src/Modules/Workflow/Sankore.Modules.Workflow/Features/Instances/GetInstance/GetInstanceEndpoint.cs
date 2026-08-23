using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetInstance;

internal static class GetInstanceEndpoint
{
    public static IEndpointRouteBuilder MapGetInstance(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}", Handle)
            .WithName("GetWorkflowInstance")
            .WithSummary("Get a workflow instance with all its steps")
            .RequireAuthorization(Permissions.CanReadWorkflow.Code)
            .Produces<WorkflowInstanceDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetInstanceQuery(id), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(result.Error);
    }
}
