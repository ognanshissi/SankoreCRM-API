using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

public static class UpdateUserEndpoint
{
    public static IEndpointRouteBuilder MapUpdateUser(this IEndpointRouteBuilder app)
    {
        app.MapPut("{userId:guid}", Handle)
            .WithTags("Users")
            .WithName("UpdateUser")
            .WithSummary("Update user details")
            .WithDescription("Partial update: only provided fields are applied. Requires permission: user:update.")
            .RequireAuthorization(Permissions.CanUpdateUser.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid userId,
        UpdateUserRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateUserCommand(
            userId,
            req.FullName,
            req.AgencyId,
            req.SpokenLanguages,
            req.Specialties,
            req.EnableNotifications), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record UpdateUserRequest(
    string? FullName,
    Guid? AgencyId,
    List<string>? SpokenLanguages,
    List<string>? Specialties,
    bool? EnableNotifications);
