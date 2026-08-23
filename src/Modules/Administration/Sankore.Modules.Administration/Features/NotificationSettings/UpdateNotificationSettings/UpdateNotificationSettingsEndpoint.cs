using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

internal static class UpdateNotificationSettingsEndpoint
{
    public static IEndpointRouteBuilder MapUpdateNotificationSettings(this IEndpointRouteBuilder app)
    {
        app.MapPut("", Handle)
            .WithName("UpdateNotificationSettings")
            .WithSummary("Configure the email provider for the current tenant")
            .RequireAuthorization(Permissions.CanManageNotificationSettings.Code)
            .Accepts<UpdateNotificationSettingsCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        UpdateNotificationSettingsCommand command,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}
