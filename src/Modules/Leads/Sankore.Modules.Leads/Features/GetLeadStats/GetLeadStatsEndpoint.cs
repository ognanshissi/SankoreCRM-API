namespace Sankore.Modules.Leads.Features.GetLeadStats;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetLeadStatsEndpoint
{
    public static IEndpointRouteBuilder MapGetLeadStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("stats", Handle)
            .WithName("GetLeadStats")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadStatsDto>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var result = await sender.Send(new GetLeadStatsQuery(from, to), ct);
        return Results.Ok(result.Value);
    }
}
