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
        app.MapPost("/auth/activate", Handle)
            .WithTags("Auth")
            .WithName("ActivateAccount")
            .AllowAnonymous()
            .WithTenantHeader();
        return app;
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
