namespace Sankore.Modules.Kyc.Features.Routing;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Routing.Controls.ListControls;
using Sankore.Modules.Kyc.Features.Routing.Controls.RecordControl;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice — the repo's vertical-slice rule.
///
/// <para>
/// <b>There is no route for the routing itself</b>, and there must not be.
/// <c>RouteRemoteKycFileCommand</c> is the one command in the module that can grant a KYC tier
/// with no human approver, and the only thing entitled to send it is the verification outcome of a
/// file the customer submitted themselves. An endpoint — however permissioned — would turn "the
/// machine decided" into "somebody asked the machine to decide", which is a different act with a
/// different accountability.
/// </para>
///
/// <para>
/// What IS routed here is the control of those decisions (KRM-B-11). The group is
/// <c>kyc-files</c> like the other areas, so the queue reads
/// <c>GET /api/v1/kyc-files/auto-approval-controls</c>: a control is a fact about a file, not a
/// resource of its own.
/// </para>
/// </summary>
internal static class KycRoutingEndpoints
{
    internal static IEndpointRouteBuilder MapKycRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapListKycAutoApprovalControls();
        group.MapRecordKycAutoApprovalControl();

        return app;
    }
}
