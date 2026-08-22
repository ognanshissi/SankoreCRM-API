using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Users.Register;

public static class RegisterEndpoint
{
    public static IEndpointRouteBuilder MapRegister(this IEndpointRouteBuilder app)
    {
        app.MapPost("create-root", Handle)
            .WithName("Create Root")
            .AllowAnonymous()
            .WithTenantHeader();

        return app;
    }

    public static async Task<IResult> Handle(
        RegisterRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RegisterCommand(req.Email, req.Password, req.ConfirmPassword, req.FirstName,  req.LastName),
            ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/users/{result.Value.UserId}", result.Value)
            : Results.Problem(result.Error, statusCode: 400);
    }
}

public sealed record RegisterRequest(
    string Email,
    string Password,
    string ConfirmPassword,
    string FirstName,
    string LastName);
