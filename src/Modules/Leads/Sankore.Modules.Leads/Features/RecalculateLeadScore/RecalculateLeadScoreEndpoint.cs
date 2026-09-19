namespace Sankore.Modules.Leads.Features.RecalculateLeadScore;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class RecalculateLeadScoreEndpoint
{
    public static IEndpointRouteBuilder MapRecalculateLeadScore(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/recalculate-score", Handle)
            .WithName("RecalculateLeadScore")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<RecalculateLeadScoreResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RecalculateLeadScoreCommand(leadId, "MANUAL_RECALCULATION"), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Score recalculation failed", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }
}
