using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.DeactivateUser;

public static class DeactivateUserEndpoint
{
    public static IEndpointRouteBuilder MapDeactivateUser(this IEndpointRouteBuilder app)
    {
        // PUT .../users/{userId}/deactivate — state transition, not a physical delete.
        app.MapPut("{userId:guid}/deactivate", Handle)
            .WithName("DeactivateUser")
            .WithSummary("Logically deactivate a user")
            .WithDescription(
                "Sets the user status to Disabled and revokes all active role assignments. " +
                "The user record is never deleted. A UserDeactivatedEvent is published so the " +
                "Leads module can automatically reassign the agent's active leads. " +
                "Requires permission: user:deactivate.")
            .RequireAuthorization(Permissions.CanDeactivateUser.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid userId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateUserCommand(userId), ct);

        if (!result.IsSuccess)
        {
            // Distinguish "not found" from domain rule violations for precise HTTP status.
            var isNotFound = result.Error!.Contains("not found", StringComparison.OrdinalIgnoreCase);
            return isNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}
