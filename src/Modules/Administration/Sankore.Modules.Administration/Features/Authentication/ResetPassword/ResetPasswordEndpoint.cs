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
        app.MapPost("/auth/reset-password", Handle)
            .WithTags("Auth")
            .WithName("ResetPassword")
            .AllowAnonymous()
            .WithTenantHeader();
        return app;
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
