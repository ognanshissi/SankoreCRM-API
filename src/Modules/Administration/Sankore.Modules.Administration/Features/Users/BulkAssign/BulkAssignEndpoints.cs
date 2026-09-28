using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

public static class BulkAssignEndpoints
{
    public static IEndpointRouteBuilder MapBulkAssign(this IEndpointRouteBuilder app)
    {
        app.MapPost("bulk/agency", AssignAgency)
            .WithName("BulkAssignUsersToAgency")
            .WithSummary("Assign a selection of users to one agency")
            .WithDescription(
                "Moves every selected user into the given agency. The request succeeds even when "
                + "some users are skipped — each one carries its own outcome and reason, so a single "
                + "ineligible row in a grid selection does not force the operator to start again. "
                + "Skipped when: the user does not exist, is the system account, already belongs to "
                + "that agency, or currently manages another agency (vacate that post first). "
                + "At most 200 users per request. Requires permission: user:assign-agency.")
            .RequireAuthorization(Permissions.CanAssignUserAgency.Code)
            .Produces<BulkAssignResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("bulk/role", AssignRole)
            .WithName("BulkAssignRoleToUsers")
            .WithSummary("Give the same role to a selection of users")
            .WithDescription(
                "Grants one role to every selected user, through ASP.NET Identity so it reaches "
                + "their next token, and records the grant against the operator. Per-user outcomes "
                + "as above; skipped when the user does not exist, is the system account, or already "
                + "holds the role. At most 200 users per request. Requires permission: user:assign-role.")
            .RequireAuthorization(Permissions.CanAssignRole.Code)
            .Produces<BulkAssignResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> AssignAgency(
        BulkAssignAgencyRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new BulkAssignAgencyCommand(req.UserIds, req.AgencyId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error!);
    }

    private static async Task<IResult> AssignRole(
        BulkAssignRoleRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new BulkAssignRoleCommand(req.UserIds, req.RoleId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error!);
    }

    /// <summary>
    /// Only the shared target can fail the request as a whole; anything user-specific is a line
    /// in the report, not a status code.
    /// </summary>
    private static IResult ToProblem(string error) => error switch
    {
        BulkAssignErrors.AgencyNotFound or BulkAssignErrors.RoleNotFound
            => Results.NotFound(new { error }),
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}

public sealed record BulkAssignAgencyRequest(IReadOnlyList<Guid> UserIds, Guid AgencyId);

public sealed record BulkAssignRoleRequest(IReadOnlyList<Guid> UserIds, Guid RoleId);
