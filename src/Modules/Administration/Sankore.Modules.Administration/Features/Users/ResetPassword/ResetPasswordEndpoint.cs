using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.ResetPassword;

public static class ResetPasswordEndpoint
{
    public static IEndpointRouteBuilder MapResetPassword(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/reset-password", Handle)
            .WithName("ResetPassword")
            .WithSummary("Reset a user's password (admin action)")
            .WithDescription(
                "Resets the user's password and extends the expiry by 90 days. " +
                "Reuse of any of the last 12 passwords is rejected with PASSWORD_RECENTLY_USED. " +
                "Requires permission: user:reset-password.")
            .RequireAuthorization(Permissions.CanResetPassword.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid userId,
        ResetPasswordRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ResetPasswordCommand(userId, req.NewPassword), ct);

        if (!result.IsSuccess)
        {
            var isNotFound = result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase);
            return isNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}

public sealed record ResetPasswordRequest(string NewPassword);
