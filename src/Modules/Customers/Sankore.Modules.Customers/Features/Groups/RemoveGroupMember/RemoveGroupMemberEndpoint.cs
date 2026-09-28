namespace Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RemoveGroupMemberEndpoint
{
    public static IEndpointRouteBuilder MapRemoveGroupMember(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{groupId:guid}/members/{clientId:guid}", Handle)
            .WithName("RemoveClientGroupMember")
            .WithSummary("Close a client's membership in a group")
            .WithDescription(
                "Closes the membership (LeftAt + LeaveReason); the row is never deleted, so the group's " +
                "joint-liability history stays auditable. " +
                "The mandatory reason travels as the query parameter 'reason' because DELETE bodies are " +
                "dropped by several HTTP clients and proxies; a blank one returns REASON_REQUIRED. " +
                "If an Active group falls under group-min-size-<type>, ClientUnderMinimumGroupSizeEvent is " +
                "published and the response carries belowMinimumSize=true — the group's status is NOT changed. " +
                "404: GROUP_NOT_FOUND (also when outside the caller's perimeter) or MEMBERSHIP_NOT_FOUND. " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<RemoveGroupMemberResult>(StatusCodes.Status200OK)
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
        ISender sender,
        string? reason,
        uint? expectedVersion,
        CancellationToken ct)
    {
        var result = await sender.Send(new RemoveGroupMemberCommand(
            GroupId: groupId,
            ClientId: clientId,
            Reason: reason ?? string.Empty,
            ExpectedVersion: expectedVersion), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNotFound or CustomerErrors.MembershipNotFound
                => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.InvalidStatusTransition
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                title: "Removing the member failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}
