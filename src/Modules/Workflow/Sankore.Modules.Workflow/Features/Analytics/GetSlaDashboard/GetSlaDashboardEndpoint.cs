using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetSlaDashboard;

internal static class GetSlaDashboardEndpoint
{
    internal static IEndpointRouteBuilder MapGetSlaDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("sla", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            ISender sender,
            CancellationToken ct) =>
        {
            var now      = DateTimeOffset.UtcNow;
            var fromDate = from ?? now.AddDays(-30);
            var toDate   = to   ?? now;

            if (fromDate >= toDate)
                return Results.BadRequest(new { error = "from must be before to." });

            var result = await sender.Send(new GetSlaDashboardQuery(fromDate, toDate), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Problem(result.Error);
        })
        .WithName("GetSlaDashboard")
        .WithSummary("SLA breach metrics: breach rate per template, daily breach trend, currently overdue active steps.")
        .RequireAuthorization(Permissions.CanViewWorkflowAnalytics.Code)
        .Produces<SlaDashboardDto>()
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public sealed record SlaDashboardDto(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<SlaTemplateBreachDto> ByTemplate,
    IReadOnlyList<DailyBreachDto> DailyTrend,
    IReadOnlyList<OverdueStepDto> OverdueSteps);

public sealed record SlaTemplateBreachDto(
    Guid TemplateId,
    string TemplateName,
    int TotalSteps,
    int BreachedSteps,
    double BreachRate);

public sealed record DailyBreachDto(
    DateTime Date,
    int BreachCount);

public sealed record OverdueStepDto(
    Guid StepId,
    Guid InstanceId,
    string EntityType,
    Guid EntityId,
    string StepName,
    DateTimeOffset DueAt,
    double OverdueByHours,
    Guid? AssignedToUserId,
    string? ApproverRoleCode);
