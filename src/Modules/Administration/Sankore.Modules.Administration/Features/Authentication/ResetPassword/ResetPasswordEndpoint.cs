using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

internal static class ResetPasswordEndpoint
{
    internal static IEndpointRouteBuilder MapResetPassword(this IEndpointRouteBuilder app)
    {
        // Step 1: check the link before showing the form. Does NOT consume the token.
        app.MapGet("/auth/reset-password/verify", HandleValidate)
            .WithTags("Auth")
            .WithName("ValidateResetToken")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .AllowAnonymous()
            .WithOpenApi()
            .WithTenantHeader()
            .RequireRateLimiting("auth");

        // Step 2: set the new password (consumes the token)
        app.MapPost("/auth/reset-password", Handle)
            .WithTags("Auth")
            .WithName("ResetPassword")
            .Produces<ResetPasswordResult>(StatusCodes.Status200OK)
            .AllowAnonymous()
            .WithOpenApi()
            .WithTenantHeader()
            .RequireRateLimiting("auth");
        return app;
    }

    private static async Task<IResult> HandleValidate(
        string userId, string token, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ValidateResetTokenQuery(userId, token), ct);

        return result.IsSuccess
            ? Results.Ok()
            : Results.Problem(result.Error, statusCode: 400);
    }

    private static async Task<IResult> Handle(
        ResetPasswordRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new ResetPasswordCommand(req.UserId, req.Token, req.NewPassword, req.ConfirmPassword), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 400);
    }
}

internal sealed record ResetPasswordRequest(
    string UserId,
    string Token,
    string NewPassword,
    string ConfirmPassword);
