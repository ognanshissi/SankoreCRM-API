using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AdminResetPassword;

internal static class AdminResetPasswordEndpoint
{
    internal static IEndpointRouteBuilder MapAdminResetPassword(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/reset-password", async (
            Guid userId,
            AdminResetPasswordRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(
                new AdminResetPasswordCommand(userId, req.NewPassword, req.ConfirmPassword), ct);

            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("AdminResetPassword")
        .WithSummary("Admin-initiated password reset for another user. Target user's password expires immediately — they are forced to change it on next login. Requires user:reset-password.")
        .WithTags("Users")
        .RequireAuthorization(Permissions.CanResetPassword.Code)
        .Produces(StatusCodes.Status204NoContent)
        .Produces<object>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

internal sealed record AdminResetPasswordRequest(string NewPassword, string ConfirmPassword);
