namespace Sankore.Modules.Kyc.Features.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Settings.GetKycSetting;
using Sankore.Modules.Kyc.Features.Settings.ListKycSettings;
using Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Same shape as M01's <c>customer-settings</c>,
/// because these are the same kind of object — a closed, typed list of per-tenant parameters owned
/// by the module that enforces them.
///
/// <para>
/// Until this area existed, the M02 parameters were only reachable with SQL. The ceilings of
/// KYC-B-06 were therefore server-side in name only: changing one meant a database session, which is
/// precisely why the front ended up carrying 250 000 and 500 000 as constants.
/// </para>
/// </summary>
internal static class SettingsEndpoints
{
    internal static IEndpointRouteBuilder MapKycSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-settings").WithTags("KYC Settings");

        group.MapListKycSettings();
        group.MapGetKycSetting();
        group.MapUpdateKycSetting();

        return app;
    }
}
