namespace Sankore.Modules.Leads.Features.GetSlaBreaches;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetSlaBreachesEndpoint
{
    public static IEndpointRouteBuilder MapGetSlaBreaches(this IEndpointRouteBuilder app)
    {
        app.MapGet("sla-breaches", Handle)
            .WithName("GetLeadSlaBreaches")
            .WithTags("Leads — Analytics")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<SlaBreachDto>>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        Guid? agentId = null,
        Guid? agencyId = null)
    {
        var result = await sender.Send(new GetSlaBreachesQuery(agentId, agencyId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "SLA breach query failed", detail: result.Error, statusCode: 500);
    }
}
