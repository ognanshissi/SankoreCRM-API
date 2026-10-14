namespace Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateSmsSettingsEndpoint
{
    public static IEndpointRouteBuilder MapUpdateSmsSettings(this IEndpointRouteBuilder app)
    {
        app.MapPut("", Handle)
            .WithName("UpdateSmsSettings")
            .WithSummary("Configure the SMS provider for the current tenant")
            .WithDescription(
                "Disabled, Brevo or Twilio. The credential (Brevo API key, Twilio Auth Token) is "
                + "written to the secrets vault and never to a column; leave it null to keep the "
                + "one already stored. Switching provider without supplying one leaves the new "
                + "provider without a credential — a key belongs to the provider it was issued "
                + "for. Disabling keeps the sender id and the stored key, so the channel can be "
                + "turned back on without re-entering them. Requires notification:settings:manage.")
            .RequireAuthorization(Permissions.CanManageNotificationSettings.Code)
            .Accepts<UpdateSmsSettingsCommand>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, UpdateSmsSettingsCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }
}
