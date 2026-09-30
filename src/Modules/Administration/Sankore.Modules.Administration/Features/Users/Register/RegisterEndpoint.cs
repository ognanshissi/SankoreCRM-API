using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;

namespace Sankore.Modules.Administration.Features.Users.Register;

public static class RegisterEndpoint
{
    public static IEndpointRouteBuilder MapRegister(this IEndpointRouteBuilder app)
    {
        // AllowAnonymous because this endpoint is what creates the first account of a tenant:
        // there is no JWT to present yet. RequireApiKey puts it behind the same shared secret the
        // tenant registry uses, so provisioning stays a server-to-server operation — and outside
        // Development it is refused outright when no key is configured. Before that, anyone who
        // reached the API first could claim the system user of any tenant that had none.
        app.MapPost("create-root", Handle)
            .WithName("Create Root")
            .WithSummary("Provision the system user of a tenant (requires X-Api-Key)")
            .AllowAnonymous()
            .RequireApiKey();

        return app;
    }

    public static async Task<IResult> Handle(
        RegisterRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RegisterCommand(req.Email, req.Password, req.ConfirmPassword, req.FirstName, req.LastName, req.TenantId),
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
    string LastName,
    Guid TenantId);
