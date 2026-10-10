namespace Sankore.Modules.Integration.Features.Balance.GetLiveBalance;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetLiveBalanceEndpoint
{
    internal static IEndpointRouteBuilder MapGetLiveBalance(this IEndpointRouteBuilder app)
    {
        app.MapGet("{crmCustomerId:guid}", Handle)
            .WithName("GetLiveBalance")
            .WithSummary("Read an account balance from the core banking system")
            .WithDescription(
                "Calls the tenant's core banking system for the balance of one account of one "
                + "client, and caches a live answer for 60 seconds per account. "
                + "isStale is the field to render: false means the figure comes from the CBS "
                + "(now, or within the last 60 seconds); true means the CBS could not be reached "
                + "— the circuit breaker is open, the call failed, or the installation serves "
                + "balances in batch only — and the figure is the last synchronised one, true as "
                + "of asOf. Never show a stale figure as current. "
                + "Restricted to the clients of the caller's own agency perimeter. A client that "
                + "does not exist, one outside that perimeter, an account that is not the "
                + "client's, and an account no figure is held for ALL answer 404 "
                + "INTEGRATION_BALANCE_NOT_AVAILABLE — never 403, so the response cannot be used "
                + "to probe for clients or accounts in other branches. "
                + "Requires permission: CoreBanking.Balance.ViewLive.")
            .RequireAuthorization(Permissions.CanViewLiveBalance.Code)
            .Produces<LiveBalanceDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    /// <summary>
    /// <paramref name="accountRef"/> is a query parameter and not a second route segment: an
    /// external account reference is whatever the CBS says it is (see <c>ExternalId</c>), and
    /// several core banking systems format one with a slash. A route segment would answer 404 for
    /// those accounts with no clue why. <c>GetIntegrationReference</c> carries its string
    /// identifiers the same way.
    /// </summary>
    private static async Task<IResult> Handle(
        Guid crmCustomerId,
        [FromQuery] string accountRef,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetLiveBalanceQuery(crmCustomerId, accountRef), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        // One failure and one status code. A blank accountRef lands here too, as a 404 rather than
        // a 400: the handler cannot tell it apart from an account this client does not hold, and
        // inventing the distinction at the edge would be the leak the single code avoids.
        return Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound);
    }
}
