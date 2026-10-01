namespace Sankore.Modules.Kyc.Features.Verification;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Verification.GetVerification;
using Sankore.Modules.Kyc.Features.Verification.RunVerification;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// The group is <c>kyc-files</c> rather than <c>verifications</c>: a verification is not a resource
/// of its own, it is something done to a file, and the route says so —
/// <c>POST /api/v1/kyc-files/{id}/verify</c>. Two aggregators mapping the same prefix is fine;
/// <c>MapGroup</c> builds a route prefix, not a registry.
/// </summary>
internal static class VerificationEndpoints
{
    internal static IEndpointRouteBuilder MapKycVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapRunKycVerification();
        group.MapGetKycVerification();

        return app;
    }
}
