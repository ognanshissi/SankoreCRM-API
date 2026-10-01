namespace Sankore.Modules.Kyc.Features.Approval;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Approval.Decide;
using Sankore.Modules.Kyc.Features.Approval.GetCircuit;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// The group is <c>kyc-files</c> and not <c>approvals</c>: an approval is not a resource of its
/// own, it is something done to a file, and the route says so —
/// <c>POST /api/v1/kyc-files/{id}/approval/decisions</c>. Two aggregators mapping the same prefix
/// is fine; <c>MapGroup</c> builds a route prefix, not a registry.
///
/// There is no route for starting a circuit. <c>StartKycApprovalCommand</c> is sent by the
/// verification outcome and by nothing a human can click.
/// </summary>
internal static class KycApprovalEndpoints
{
    internal static IEndpointRouteBuilder MapKycApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapGetKycApprovalCircuit();
        group.MapDecideKycApproval();

        return app;
    }
}
