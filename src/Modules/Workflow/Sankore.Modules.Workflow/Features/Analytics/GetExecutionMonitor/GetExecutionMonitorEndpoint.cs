using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetExecutionMonitor;

internal static class GetExecutionMonitorEndpoint
{
    internal static IEndpointRouteBuilder MapGetExecutionMonitor(this IEndpointRouteBuilder app)
    {
        app.MapGet("monitor", async (
            int? stuckThresholdHours,
            ISender sender,
            CancellationToken ct) =>
        {
            var query  = new GetExecutionMonitorQuery(stuckThresholdHours ?? 48);
            var result = await sender.Send(query, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Problem(result.Error);
        })
        .WithName("GetExecutionMonitor")
        .WithSummary("Real-time execution health: active instances per template, queue depths, stuck instances with no SLA deadline.")
        .RequireAuthorization(Permissions.CanViewWorkflowAnalytics.Code)
        .Produces<ExecutionMonitorDto>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public sealed record ExecutionMonitorDto(
    DateTimeOffset AsOf,
    int StuckThresholdHours,
    IReadOnlyList<TemplateQueueDto> ByTemplate,
    IReadOnlyList<StuckInstanceDto> StuckInstances);

public sealed record TemplateQueueDto(
    Guid TemplateId,
    string TemplateName,
    int ActiveInstances,
    int WaitingForChild,
    int QueueDepth = 0);

public sealed record StuckInstanceDto(
    Guid InstanceId,
    string EntityType,
    Guid EntityId,
    Guid TemplateId,
    string TemplateName,
    DateTimeOffset StartedAt,
    double StuckForHours);
