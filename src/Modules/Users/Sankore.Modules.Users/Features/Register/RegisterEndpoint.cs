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
        app.MapPost("/api/auth/register", Handle)
            .WithTags("Identity")
            .WithName("Register");
        
        return app;
    }

    public async static Task<IResult> Handle(RegisterRequest req, ISender sender,  HttpContext http, CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();
        
        var result = await sender.Send(new RegisterCommand(tenantId, req.AgencyId, req.Email, req.Password, req.ConfirmPassword, req.FullName), ct);

        return result.IsSuccess 
            ? Results.Created("", result.Value) 
            : Results.Problem("Failed to register user");
    }
}

public sealed record RegisterRequest(Guid AgencyId, string Email, string Password, string ConfirmPassword,  string FullName);