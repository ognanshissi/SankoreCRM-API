namespace Sankore.Modules.Leads.Features.GetFunnelMetrics;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetFunnelMetricsEndpoint
{
    public static IEndpointRouteBuilder MapGetFunnelMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("funnel", Handle)
            .WithName("GetLeadFunnelMetrics")
            .WithTags("Leads — Analytics")
            .RequireAuthorization(Permissions.CanViewLeadAnalytics.Code)
            .Produces<FunnelMetricsDto>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? agencyId = null)
    {
        var result = await sender.Send(new GetFunnelMetricsQuery(from, to, agencyId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Funnel metrics query failed", detail: result.Error, statusCode: 500);
    }
}
