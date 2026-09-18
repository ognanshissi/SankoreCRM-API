using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sankore.Modules.Administration.Features.Users.ChangePassword;

internal static class ChangePasswordEndpoint
{
    internal static IEndpointRouteBuilder MapChangePassword(this IEndpointRouteBuilder app)
    {
        app.MapPost("me/change-password", async (
            ChangePasswordRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(
                new ChangePasswordCommand(req.NewPassword, req.ConfirmPassword), ct);

            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("ChangePassword")
        .WithSummary("Allows the authenticated user to set a new password. No reset token required. Enforces password-history policy (last 12 passwords).")
        .WithTags("Users")
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent)
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}

internal sealed record ChangePasswordRequest(string NewPassword, string ConfirmPassword);
