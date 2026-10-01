namespace Sankore.Modules.Kyc.Features.Corrections;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Corrections.CorrectField;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// The group is <c>kyc-files</c> rather than <c>corrections</c>: a correction has no life of its
/// own, it is always a fact about one file, and the URL says so.
/// </summary>
internal static class KycCorrectionsEndpoints
{
    internal static IEndpointRouteBuilder MapKycCorrectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapCorrectKycField();

        return app;
    }
}
