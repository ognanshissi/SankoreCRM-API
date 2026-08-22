using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration.Features.Users.CreateUser;

public static class CreateUserEndpoint
{
    public static IEndpointRouteBuilder MapCreateUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("create", Handle)
            .WithTags("Users")
            .WithName("CreateUser")
            .WithSummary("Create a new user (admin action)")
            .WithDescription(
                "Creates a user account in PendingActivation status. " +
                "An activation email is sent so the user can set their first password. " +
                "Requires permission: user:create.")
            .RequireAuthorization("Users.Create")
            .Produces<CreateUserResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateUserRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var callerId = http.User.GetUserId();

        var result = await sender.Send(new CreateUserCommand(
            AgencyId: req.AgencyId,
            RoleId: req.RoleId,
            FullName: req.FullName,
            Email: req.Email,
            DefaultLanguage: req.DefaultLanguage,
            SpokenLanguages: req.SpokenLanguages,
            Specialties: req.Specialties,
            CallerUserId: callerId), ct);

        return result.IsSuccess
            ? Results.Created($"/api/administration/users/{result.Value.UserId}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record CreateUserRequest(
    Guid AgencyId,
    Guid RoleId,
    string FullName,
    string Email,
    string DefaultLanguage,
    List<string> SpokenLanguages,
    List<string> Specialties);
