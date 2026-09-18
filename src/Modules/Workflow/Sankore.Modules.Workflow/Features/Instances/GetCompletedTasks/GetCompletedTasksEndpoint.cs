using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetCompletedTasks;

internal static class GetCompletedTasksEndpoint
{
    internal static IEndpointRouteBuilder MapGetCompletedTasks(this IEndpointRouteBuilder app)
    {
        // ── Own completed tasks ─────────────────────────────────────────────
        app.MapGet("my-completed-tasks", async (
            int? page,
            int? pageSize,
            ICurrentUser currentUser,
            ISender sender,
            CancellationToken ct) =>
        {
            var query  = new GetCompletedTasksQuery(currentUser.Id, page ?? 1, pageSize ?? 20);
            var result = await sender.Send(query, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("GetMyCompletedTasks")
        .WithSummary("Paginated history of workflow tasks the current user has completed (approved, rejected, or was assigned to when they resolved).")
        .RequireAuthorization()
        .Produces<PagedResult<CompletedTaskDto>>()
        .Produces(StatusCodes.Status401Unauthorized);

        // ── Any user's completed tasks (admin / manager view) ───────────────
        app.MapGet("users/{userId:guid}/completed-tasks", async (
            Guid userId,
            int? page,
            int? pageSize,
            ISender sender,
            CancellationToken ct) =>
        {
            var query  = new GetCompletedTasksQuery(userId, page ?? 1, pageSize ?? 20);
            var result = await sender.Send(query, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.BadRequest(new { error = result.Error });
        })
        .WithName("GetUserCompletedTasks")
        .WithSummary("Paginated history of workflow tasks completed by a specific user. Requires workflow:instance:view.")
        .RequireAuthorization(Permissions.CanViewWorkflowInstances.Code)
        .Produces<PagedResult<CompletedTaskDto>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

// ─── DTO ──────────────────────────────────────────────────────────────────────

public sealed record CompletedTaskDto(
    Guid StepId,
    Guid InstanceId,
    string TemplateName,
    string EntityType,
    Guid EntityId,
    string StepName,
    int StepOrder,
    string Status,
    string? Comment,
    Guid? ActedByUserId,
    Guid? AssignedToUserId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
