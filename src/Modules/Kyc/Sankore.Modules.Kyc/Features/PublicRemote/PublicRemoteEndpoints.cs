namespace Sankore.Modules.Kyc.Features.PublicRemote;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.PublicRemote.Identify;
using Sankore.Modules.Kyc.Features.PublicRemote.LinkStatus;
using Sankore.Modules.Kyc.Features.PublicRemote.Otp;
using Sankore.Modules.Kyc.Features.PublicRemote.Session;

/// <summary>
/// The customer-facing surface of remote KYC (KRM-B-05 to B-07), mounted by the host under
/// <c>api/v1/public/kyc</c>.
///
/// <para>
/// <b>Its own aggregator, and its own place in the URL space, on purpose.</b> Every other endpoint
/// of this module sits under <c>api/v1/kyc-files</c> behind a permission. These three — status,
/// identify, session — are reachable by anybody on the internet, and the one thing that must never
/// happen is an authenticated-by-default group quietly covering them, or a new back-office endpoint
/// landing in this group by accident. The <c>public</c> prefix is also what
/// <c>TenantResolutionMiddleware</c> keys its exemption on, which is why the tenant check lives in
/// <c>KycRemoteGate</c> instead.
/// </para>
///
/// <para>
/// The rate-limiting policy is applied by the HOST, next to where it defines it: these routes are
/// this module's, but the window and the partition are a deployment concern.
/// </para>
/// </summary>
public static class PublicRemoteEndpoints
{
    /// <summary>
    /// Route prefix, relative to <c>api/v1/public</c>. A constant because the host mounts it and
    /// <c>KycRemoteLinkTokens.BuildLink</c> has to agree with the Angular route that serves it.
    /// </summary>
    public const string GroupPrefix = "kyc";

    internal static IEndpointRouteBuilder MapKycPublicRemoteEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(GroupPrefix).WithTags("KYC public");

        group.MapKycLinkStatus();
        group.MapKycIdentify();
        group.MapKycOtpEndpoints();
        group.MapKycRemoteSessionEndpoints();

        return app;
    }
}
