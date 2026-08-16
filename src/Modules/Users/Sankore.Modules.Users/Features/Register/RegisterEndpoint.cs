using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Features.Register;

public static class RegisterEndpoint
{
    public static IEndpointRouteBuilder MapRegister(this IEndpointRouteBuilder app)
    {
        app.MapPost("user/register", Handle)
            .WithTags("Identity")
            .WithName("Register")
            .AllowAnonymous()
            .WithTenantHeader();

        return app;
    }

    public static async Task<IResult> Handle(
        RegisterRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RegisterCommand(req.TenantId, req.AgencyId, req.Email, req.Password, req.ConfirmPassword, req.FirstName,  req.LastName, req.Role),
            ct);

        return result.IsSuccess
            ? Results.Created("", result.Value)
            : Results.Problem(result.Error, statusCode: 400);
    }
}

public sealed record RegisterRequest(
    Guid TenantId,
    Guid AgencyId,
    string Email,
    string Password,
    string ConfirmPassword,
    string FirstName,
    string LastName,
    string Role);
