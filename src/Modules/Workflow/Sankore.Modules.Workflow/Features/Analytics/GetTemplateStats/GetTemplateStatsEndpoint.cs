using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetTemplateStats;

internal static class GetTemplateStatsEndpoint
{
    internal static IEndpointRouteBuilder MapGetTemplateStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("templates/{templateId:guid}/stats", async (
            Guid templateId,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new GetTemplateStatsQuery(templateId), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.NotFound(new { error = result.Error });
        })
        .WithName("GetTemplateStats")
        .WithSummary("Aggregate statistics for a workflow template: instance counts, avg completion time, SLA breaches, per-step durations.")
        .RequireAuthorization(Permissions.CanViewWorkflowAnalytics.Code)
        .Produces<TemplateStatsDto>()
        .Produces<object>(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

// ─── DTOs ──────────────────────────────────────────────────────────────────

public sealed record TemplateStatsDto(
    Guid TemplateId,
    string EntityType,
    string TemplateName,
    int Version,
    int TotalInstances,
    Dictionary<string, int> ByStatus,
    double? AvgCompletionHours,
    int SlaBreachCount,
    IReadOnlyList<StepStatsDto> StepStats);

public sealed record StepStatsDto(
    Guid StepDefinitionId,
    string StepName,
    double? AvgDurationHours,
    int CompletedCount,
    int SkippedCount,
    int TimedOutCount,
    int RejectedCount);
