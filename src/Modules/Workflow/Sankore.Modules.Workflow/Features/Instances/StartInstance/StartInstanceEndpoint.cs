using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.StartInstance;

internal static class StartInstanceEndpoint
{
    public static IEndpointRouteBuilder MapStartInstance(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("StartWorkflowInstance")
            .WithSummary("Start a workflow instance for an entity")
            .RequireAuthorization(Permissions.CanStartWorkflow.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        StartInstanceRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new StartInstanceCommand(req.EntityType, req.EntityId), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/workflow/instances/{result.Value}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record StartInstanceRequest(string EntityType, Guid EntityId);
