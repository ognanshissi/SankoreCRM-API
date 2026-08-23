using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ListInstances;

internal static class ListInstancesEndpoint
{
    public static IEndpointRouteBuilder MapListInstances(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListWorkflowInstances")
            .WithSummary("List workflow instances (filterable by entityType, entityId, status)")
            .RequireAuthorization(Permissions.CanReadWorkflow.Code)
            .Produces<List<WorkflowInstanceDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        string? entityType, Guid? entityId, string? status,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListInstancesQuery(entityType, entityId, status), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status500InternalServerError);
    }
}
