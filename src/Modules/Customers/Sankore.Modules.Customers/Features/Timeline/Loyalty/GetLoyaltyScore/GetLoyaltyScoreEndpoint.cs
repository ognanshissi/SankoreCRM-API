namespace Sankore.Modules.Customers.Features.Timeline.Loyalty.GetLoyaltyScore;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetLoyaltyScoreEndpoint
{
    public static IEndpointRouteBuilder MapGetLoyaltyScore(this IEndpointRouteBuilder app)
    {
        app.MapGet("{clientId:guid}/loyalty-score", Handle)
            .WithName("GetClientLoyaltyScore")
            .WithSummary("Read a client's loyalty score and its history")
            .WithDescription(
                "Returns the latest score (0..100) with its full breakdown, plus the previous "
                + "computations newest first (historyLimit, default 12, max 50). "
                + "isProvisional=true means the client is younger than 90 days: tenure and regularity "
                + "cannot be measured meaningfully yet, so the score is indicative. "
                + "unavailableComponents lists the components that contributed 0 because their module "
                + "is not wired yet (volume → M03 Savings, products → M04 Credit); the score is "
                + "normalized over the available weights, not capped by the missing ones. "
                + "Answers 404 both for an unknown client and for one outside the caller's agency "
                + "perimeter. Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientLoyaltyScoreDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        Guid clientId,
        int historyLimit,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetLoyaltyScoreQuery(clientId, historyLimit), ct);

        if (result.IsFailure)
            return result.Error == CustomerErrors.ClientNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.BadRequest(new { error = result.Error });

        return Results.Ok(result.Value);
    }
}
