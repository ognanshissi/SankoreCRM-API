using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;

internal static class GetNotificationSettingsEndpoint
{
    public static IEndpointRouteBuilder MapGetNotificationSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("GetNotificationSettings")
            .WithSummary("Get the email provider configuration for the current tenant")
            .RequireAuthorization(Permissions.CanReadNotificationSettings.Code)
            .Produces<NotificationSettingsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetNotificationSettingsQuery(), ct);
        return Results.Ok(result.Value);
    }
}
