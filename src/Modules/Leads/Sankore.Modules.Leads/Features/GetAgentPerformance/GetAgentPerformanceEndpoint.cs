namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

public static class GetAgentPerformanceEndpoint
{
    public static IEndpointRouteBuilder MapGetAgentPerformance(this IEndpointRouteBuilder app)
    {
        app.MapGet("agent-performance", Handle)
            .WithName("GetAgentPerformance")
            .WithTags("Leads — Analytics")
            .RequireAuthorization(Permissions.CanViewLeadAnalytics.Code)
            .Produces<IReadOnlyList<AgentPerformanceDto>>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? agentId = null,
        Guid? agencyId = null)
    {
        var result = await sender.Send(new GetAgentPerformanceQuery(
            from, to, agentId, agencyId,
            currentUser.Id, currentUser.Roles), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Agent performance query failed", detail: result.Error, statusCode: 500);
    }
}
