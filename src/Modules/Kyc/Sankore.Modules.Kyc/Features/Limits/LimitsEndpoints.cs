namespace Sankore.Modules.Kyc.Features.Limits;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Limits.GetCaps;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// <para>
/// The group is <c>kyc-files</c>, like the verification area, and the route reads
/// <c>GET /api/v1/kyc-files/by-customer/{customerId}/caps</c>. The front's TODO asked for
/// <c>GET /customers/{id}/kyc-caps</c>; that path is not used, for two reasons. The customer
/// resource of this solution is <c>clients/{id}</c> (M01) and not <c>customers/{id}</c>, so the
/// requested path matches nothing that already exists; and a ceiling is not a resource of its own,
/// it is read off the customer's KYC file, next to <c>kyc-files/by-customer/{customerId}</c> which
/// the same screen already calls. The front reaches it through the generated client, so the path is
/// a detail there — but a second spelling of "customer" in the URL space would not have been.
/// </para>
/// </summary>
internal static class LimitsEndpoints
{
    internal static IEndpointRouteBuilder MapKycLimitsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapGetKycCaps();

        return app;
    }
}
