using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.ListTasks;

internal static class ListTasksEndpoint
{
    internal static IEndpointRouteBuilder MapListTasks(this IEndpointRouteBuilder app)
    {
        app.MapGet("{instanceId:guid}/tasks",
            async (Guid instanceId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListTasksQuery(instanceId), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.NotFound(new { error = result.Error });
            })
        .WithName("ListWorkflowTasks")
        .WithSummary("List tasks for a workflow instance")
        .RequireAuthorization()
        .Produces<List<TaskDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .WithOpenApi();

        return app;
    }
}

internal sealed record TaskDto(
    Guid Id,
    Guid InstanceId,
    string Title,
    string? Description,
    Guid? AssignedToUserId,
    string? AssignedRoleCode,
    TaskPriority Priority,
    WorkflowTaskStatus Status,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionComment);
