using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignManager;

public static class AssignManagerEndpoints
{
    public static IEndpointRouteBuilder MapAssignManager(this IEndpointRouteBuilder app)
    {
        app.MapPut("{userId:guid}/manager", Assign)
            .WithName("AssignUserManager")
            .WithSummary("Set who a user reports to")
            .WithDescription(
                "Points the user's reporting line at another user. The manager must exist in the "
                + "tenant and be Active, but need NOT belong to the same agency — a head-office "
                + "director manages branch staff. Refused when it would close a loop in the "
                + "hierarchy. Re-sending the same manager succeeds without changing anything. "
                + "Requires permission: user:assign-manager.")
            .RequireAuthorization(Permissions.CanAssignUserManager.Code)
            .Produces<AssignManagerResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapDelete("{userId:guid}/manager", Clear)
            .WithName("ClearUserManager")
            .WithSummary("Put a user at the top of their reporting line")
            .WithDescription(
                "Clears the user's own manager. Their subordinates are untouched and keep "
                + "reporting to them. Idempotent. Requires permission: user:assign-manager.")
            .RequireAuthorization(Permissions.CanAssignUserManager.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Assign(
        Guid userId, AssignUserManagerRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new AssignManagerCommand(userId, req.ManagerUserId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<IResult> Clear(Guid userId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ClearManagerCommand(userId), ct);
        return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error!);
    }

    /// <summary>
    /// A missing person is 404. A cycle or an inactive manager is 409: the request is well formed
    /// and the caller could make it succeed by choosing another manager or activating this one.
    /// </summary>
    private static IResult ToProblem(string error) => error switch
    {
        AssignManagerErrors.UserNotFound or AssignManagerErrors.ManagerNotFound
            => Results.NotFound(new { error }),
        AssignManagerErrors.ReportingCycle
            or AssignManagerErrors.ManagerNotActive
            or AssignManagerErrors.SystemAccountImmutable
            => Results.Problem(error, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}

public sealed record AssignUserManagerRequest(Guid ManagerUserId);
