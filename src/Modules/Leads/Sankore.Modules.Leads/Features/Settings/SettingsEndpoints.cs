namespace Sankore.Modules.Leads.Features.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Settings.GetLeadSetting;
using Sankore.Modules.Leads.Features.Settings.ListLeadSettings;
using Sankore.Modules.Leads.Features.Settings.PreviewRetention;
using Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Same shape as M01's
/// <c>customer-settings</c> and M02's <c>kyc-settings</c> — three modules that each own their
/// tenant parameters because M12 exposes no generic settings store.
///
/// <para>
/// Until this area existed, the M13 parameters were reachable only with SQL. That mattered most
/// for <c>lead-retention-months</c>, which governs an irreversible nightly destruction: the
/// operational instruction "check the retention period before the first run" had nowhere to be
/// carried out. The retention preview lives here for the same reason.
/// </para>
/// </summary>
internal static class SettingsEndpoints
{
    internal static IEndpointRouteBuilder MapLeadSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("lead-settings").WithTags("Lead Settings");

        group.MapListLeadSettings();

        // Before {key}, or the literal segment is swallowed by the key route and the preview
        // answers SETTING_UNKNOWN for a setting called "retention".
        group.MapPreviewRetention();

        group.MapGetLeadSetting();
        group.MapUpdateLeadSetting();

        return app;
    }
}
