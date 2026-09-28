using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

public static class AgencyManagerEndpoints
{
    public static IEndpointRouteBuilder MapAgencyManager(this IEndpointRouteBuilder app)
    {
        app.MapPut("{id:guid}/manager", Assign)
            .WithName("AssignAgencyManager")
            .WithSummary("Assign the manager of an agency")
            .WithDescription(
                "Puts a user in charge of the agency. The user must exist in the tenant, be Active, "
                + "and belong to that agency — except a super-user, who has no agency of their own. "
                + "Re-sending the same manager succeeds without changing anything. "
                + "Requires permission: agency:assign-manager.")
            .RequireAuthorization(Permissions.CanAssignAgencyManager.Code)
            .Produces<AssignAgencyManagerResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapDelete("{id:guid}/manager", Remove)
            .WithName("RemoveAgencyManager")
            .WithSummary("Leave an agency's manager post vacant")
            .WithDescription(
                "Clears the agency's manager. Idempotent — an agency with no manager stays that way. "
                + "Requires permission: agency:assign-manager.")
            .RequireAuthorization(Permissions.CanAssignAgencyManager.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Assign(
        Guid id,
        AssignAgencyManagerRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AssignAgencyManagerCommand(id, req.ManagerUserId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : ToProblem(result.Error!);
    }

    private static async Task<IResult> Remove(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RemoveAgencyManagerCommand(id), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : ToProblem(result.Error!);
    }

    /// <summary>
    /// One place decides the status code, so the two endpoints cannot drift apart.
    /// A missing agency or user is 404; an ineligible user is 409, because the request is
    /// well-formed and the caller could make it succeed by changing the user's state.
    /// </summary>
    private static IResult ToProblem(string error) => error switch
    {
        AgencyManagerErrors.AgencyNotFound or AgencyManagerErrors.ManagerNotFound
            => Results.NotFound(new { error }),
        AgencyManagerErrors.AgencyDeleted
            or AgencyManagerErrors.ManagerNotActive
            or AgencyManagerErrors.ManagerNotInAgency
            => Results.Problem(error, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}

public sealed record AssignAgencyManagerRequest(Guid ManagerUserId);
