using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Authentication.ForgotPassword;

internal static class ForgotPasswordEndpoint
{
    internal static IEndpointRouteBuilder MapForgotPassword(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/forgot-password", Handle)
            .WithTags("Auth")
            .WithName("ForgotPassword")
            .AllowAnonymous()
            .WithTenantHeader();
        return app;
    }

    private static async Task<IResult> Handle(
        ForgotPasswordRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ForgotPasswordCommand(req.Email), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 400);
    }
}

internal sealed record ForgotPasswordRequest(string Email);
