using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignScopedPermission;

internal static class AssignScopedPermissionEndpoint
{
    internal static IEndpointRouteBuilder MapAssignScopedPermission(this IEndpointRouteBuilder app)
    {
        app.MapPost("{userId:guid}/permissions", async (
            Guid userId,
            AssignScopedPermissionRequest req,
            ISender sender,
            CancellationToken ct) =>
        {
            var result = await sender.Send(new AssignScopedPermissionCommand(
                UserId: userId,
                PermissionCode: req.PermissionCode,
                StartDate: req.StartDate,
                EndDate: req.EndDate,
                ScopeId: req.ScopeId,
                ScopeType: req.ScopeType), ct);

            return result.IsSuccess
                ? Results.Created($"/api/v1/users/{userId}/permissions/{result.Value}", new { id = result.Value })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithName("AssignScopedPermission")
        .WithSummary("Assign a scoped permission to a user")
        .WithDescription(
            "Creates a time-bounded, optionally scoped permission attribution. " +
            "ScopeType: Agency | Territory | Tenant | null (global). " +
            "ScopeId required when ScopeType is set. " +
            "Requires permission: user:assign-permission.")
        .RequireAuthorization(Permissions.CanAssignPermission.Code)
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi();

        return app;
    }
}

public sealed record AssignScopedPermissionRequest(
    string PermissionCode,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    Guid? ScopeId = null,
    string? ScopeType = null);
