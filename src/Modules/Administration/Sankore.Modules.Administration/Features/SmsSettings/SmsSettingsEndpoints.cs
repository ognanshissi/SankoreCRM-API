namespace Sankore.Modules.Administration.Features.SmsSettings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.SmsSettings.GetSmsSettings;
using Sankore.Modules.Administration.Features.SmsSettings.SetSmsMonthlyQuota;
using Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;

/// <summary>
/// Area aggregator. Its own group — <c>sms-settings</c> — beside <c>notification-settings</c>
/// rather than nested under it: the two configure different channels with different credentials
/// and different allowances, and a tenant can run one without the other.
/// </summary>
internal static class SmsSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSmsSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sms-settings").WithTags("SMS Settings");

        group.MapGetSmsSettings();
        group.MapUpdateSmsSettings();
        group.MapSetSmsMonthlyQuota();

        return app;
    }
}
