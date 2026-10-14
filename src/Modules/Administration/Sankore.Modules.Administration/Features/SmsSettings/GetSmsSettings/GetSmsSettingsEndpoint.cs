namespace Sankore.Modules.Administration.Features.SmsSettings.GetSmsSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetSmsSettingsEndpoint
{
    public static IEndpointRouteBuilder MapGetSmsSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("GetSmsSettings")
            .WithSummary("Get the SMS provider configuration for the current tenant")
            .WithDescription(
                "Answers the shape of an unconfigured channel rather than 404 for a tenant that "
                + "never opened the screen: the screen is a form and needs its options and its "
                + "hints either way. Carries no credential — only whether one is stored, and "
                + "canSend, which is the difference between configured and half-configured. "
                + "Requires notification:settings:read.")
            .RequireAuthorization(Permissions.CanReadNotificationSettings.Code)
            .Produces<SmsSettingsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetSmsSettingsQuery(), ct);
        return Results.Ok(result.Value);
    }
}
