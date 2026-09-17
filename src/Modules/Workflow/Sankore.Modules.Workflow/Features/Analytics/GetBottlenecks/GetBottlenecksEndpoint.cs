using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetBottlenecks;

internal static class GetBottlenecksEndpoint
{
    internal static IEndpointRouteBuilder MapGetBottlenecks(this IEndpointRouteBuilder app)
    {
        app.MapGet("templates/{templateId:guid}/bottlenecks", async (
            Guid templateId,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new GetBottlenecksQuery(templateId), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("GetBottlenecks")
        .WithSummary("Ranks every step in a template by a composite bottleneck score (avg duration, rejection rate, timeout rate).")
        .RequireAuthorization(Permissions.CanViewWorkflowAnalytics.Code)
        .Produces<IReadOnlyList<StepBottleneckDto>>()
        .Produces<object>(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

// ─── DTO ──────────────────────────────────────────────────────────────────────

public sealed record StepBottleneckDto(
    Guid StepDefinitionId,
    string StepName,
    int Order,
    int TotalExecutions,
    int ApprovedCount,
    int RejectedCount,
    int TimedOutCount,
    int SkippedCount,
    double AvgDurationHours,
    double RejectionRate,
    double TimeoutRate,
    double BottleneckScore);
