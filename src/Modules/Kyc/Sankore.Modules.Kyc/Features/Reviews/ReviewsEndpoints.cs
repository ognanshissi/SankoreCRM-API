namespace Sankore.Modules.Kyc.Features.Reviews;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Reviews.RaiseReview;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// The group is <c>kyc-files</c> rather than <c>reviews</c>: a review has no life of its own, it
/// is always a fact about one file, and the URL says so — same choice the corrections area made.
/// </summary>
internal static class KycReviewsEndpoints
{
    internal static IEndpointRouteBuilder MapKycReviewsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapRaiseKycReview();

        return app;
    }
}
