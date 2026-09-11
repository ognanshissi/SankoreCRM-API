using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Authentication.AccountActivation;

internal static class AccountActivationEndpoint
{
    internal static IEndpointRouteBuilder MapAccountActivation(this IEndpointRouteBuilder app)
    {
        // Step 1: validate token before showing the set-password form (does NOT consume the token)
        app.MapGet("/auth/activate/verify", HandleValidate)
            .WithTags("Auth")
            .WithName("ValidateActivationToken")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .AllowAnonymous()
            .WithTenantHeader()
            .RequireRateLimiting("auth");

        // Step 2: set password and activate (consumes the token)
        app.MapPost("/auth/activate", Handle)
            .WithTags("Auth")
            .WithName("ActivateAccount")
            .Produces<AccountActivationResult>(StatusCodes.Status200OK)
            .AllowAnonymous()
            .WithTenantHeader()
            .RequireRateLimiting("auth");

        return app;
    }

    private static async Task<IResult> HandleValidate(
        string userId, string token, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ValidateActivationTokenQuery(userId, token), ct);

        return result.IsSuccess
            ? Results.Ok()
            : Results.Problem(result.Error, statusCode: 400);
    }

    private static async Task<IResult> Handle(
        AccountActivationRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new AccountActivationCommand(req.UserId,  req.Token, req.NewPassword, req.ConfirmPassword), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: 400);
    }
}

internal sealed record AccountActivationRequest(
    string UserId,
    string Token,
    string NewPassword,
    string ConfirmPassword);
