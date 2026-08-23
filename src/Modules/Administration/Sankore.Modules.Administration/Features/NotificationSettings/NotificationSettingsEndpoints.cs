using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;
using Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;
using Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

namespace Sankore.Modules.Administration.Features.NotificationSettings;

internal static class NotificationSettingsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationSettingsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("notification-settings").WithTags("Notification Settings");

        group.MapGetNotificationSettings();
        group.MapUpdateNotificationSettings();
        group.MapSetMonthlyQuota();

        return app;
    }
}
