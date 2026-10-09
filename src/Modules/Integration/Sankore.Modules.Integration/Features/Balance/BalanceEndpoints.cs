namespace Sankore.Modules.Integration.Features.Balance;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Balance.GetLiveBalance;

/// <summary>
/// Area aggregator for the real-time balance (INT-15). One <c>MapGroup</c>, one call per slice.
///
/// <para>
/// Its own area rather than a route under <c>integration/references</c> or the command queue: this
/// is the only synchronous read in the module that reaches the core banking system while somebody
/// waits, it is the only one behind <c>CoreBanking.Balance.ViewLive</c>, and ASS-05's premium debit
/// will join it here rather than in the queue it does not use.
/// </para>
/// </summary>
internal static class BalanceEndpoints
{
    internal static IEndpointRouteBuilder MapBalanceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/balance").WithTags("Integration");

        group.MapGetLiveBalance();

        return app;
    }
}
