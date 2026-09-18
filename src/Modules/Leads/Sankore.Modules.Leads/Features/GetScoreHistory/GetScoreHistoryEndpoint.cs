namespace Sankore.Modules.Leads.Features.GetScoreHistory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetScoreHistoryEndpoint
{
    public static IEndpointRouteBuilder MapGetScoreHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/score-history", Handle)
            .WithName("GetLeadScoreHistory")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<ScoreHistoryDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetScoreHistoryQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
