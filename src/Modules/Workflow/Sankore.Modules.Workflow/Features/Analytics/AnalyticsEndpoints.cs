using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Workflow.Features.Analytics.GetTemplateStats;

namespace Sankore.Modules.Workflow.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("analytics").WithTags("WorkflowAnalytics");

        group.MapGetTemplateStats();

        return app;
    }
}
