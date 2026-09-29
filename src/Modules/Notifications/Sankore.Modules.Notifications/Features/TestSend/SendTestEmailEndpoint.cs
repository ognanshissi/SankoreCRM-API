namespace Sankore.Modules.Notifications.Features.TestSend;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class SendTestEmailEndpoint
{
    public static IEndpointRouteBuilder MapSendTestEmail(this IEndpointRouteBuilder app)
    {
        app.MapPost("test-send", Handle)
            .WithName("SendTestEmail")
            .WithSummary("Send a test email through the tenant's configured provider")
            .WithDescription(
                "Sends one message immediately, bypassing the outbox, and returns whether the "
                + "provider accepted it. On refusal the provider's own message is returned — an "
                + "invalid API key or a wrong SMTP password shows up here instead of as messages "
                + "quietly dead-lettering. Requires permission: notification:settings:manage.")
            .RequireAuthorization(Permissions.CanManageNotificationSettings.Code)
            .Produces<TestEmailResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        SendTestEmailRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SendTestEmailCommand(req.RecipientEmail), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record SendTestEmailRequest(string RecipientEmail);
