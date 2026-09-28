namespace Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class AssignOfficeRoleEndpoint
{
    public static IEndpointRouteBuilder MapAssignOfficeRole(this IEndpointRouteBuilder app)
    {
        app.MapPut("{groupId:guid}/members/{clientId:guid}/role", Handle)
            .WithName("AssignClientGroupOfficeRole")
            .WithSummary("Set a member's office role in a group")
            .WithDescription(
                "Sets the member's role. President, Treasurer and Secretary are each unique within the " +
                "group: the previous holder is demoted to Member in the same transaction and returned as " +
                "previousHolderClientId. Automatic activation is re-evaluated afterwards, so filling the " +
                "last vacant role can move the group from Forming to Active. " +
                "404: GROUP_NOT_FOUND (also when outside the caller's perimeter) or MEMBERSHIP_NOT_FOUND. " +
                "409: CONCURRENCY_CONFLICT, INVALID_STATUS_TRANSITION (dissolved group). " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<AssignOfficeRoleResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid groupId,
        Guid clientId,
        AssignOfficeRoleRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AssignOfficeRoleCommand(
            GroupId: groupId,
            ClientId: clientId,
            OfficeRole: req.OfficeRole,
            ExpectedVersion: req.ExpectedVersion), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNotFound or CustomerErrors.MembershipNotFound
                => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.InvalidStatusTransition
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                title: "Assigning the office role failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record AssignOfficeRoleRequest(GroupOfficeRole OfficeRole, uint? ExpectedVersion = null);
