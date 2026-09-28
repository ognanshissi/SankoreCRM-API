namespace Sankore.Modules.Customers.Features.Groups.AddGroupMember;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class AddGroupMemberEndpoint
{
    public static IEndpointRouteBuilder MapAddGroupMember(this IEndpointRouteBuilder app)
    {
        app.MapPost("{groupId:guid}/members", Handle)
            .WithName("AddClientGroupMember")
            .WithSummary("Add a client to a group")
            .WithDescription(
                "Adds the client with the given office role, then re-evaluates automatic activation " +
                "(Forming -> Active once group-min-size-<type> active members AND President + Treasurer + " +
                "Secretary are filled). " +
                "409 codes: GROUP_SIZE_LIMIT_REACHED (group-max-size-<type> reached), " +
                "GROUP_MEMBER_NOT_ELIGIBLE (client KycRejected, Archived or Merged), " +
                "ALREADY_IN_SOLIDARITY_GROUP (tenant setting solidarity-single-group-rule), " +
                "CONCURRENCY_CONFLICT (stale expectedVersion). " +
                "404: GROUP_NOT_FOUND (unknown or outside the caller's agency perimeter) or CLIENT_NOT_FOUND. " +
                "Re-posting an existing active member is idempotent and returns that membership. " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<AddGroupMemberResult>(StatusCodes.Status200OK)
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
        AddGroupMemberRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AddGroupMemberCommand(
            GroupId: groupId,
            ClientId: req.ClientId,
            OfficeRole: req.OfficeRole ?? GroupOfficeRole.Member,
            ExpectedVersion: req.ExpectedVersion), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNotFound or CustomerErrors.ClientNotFound
                => Results.NotFound(new { error = result.Error }),
            CustomerErrors.GroupSizeLimitReached
                or CustomerErrors.GroupMemberNotEligible
                or CustomerErrors.AlreadyInSolidarityGroup
                or CustomerErrors.ConcurrencyConflict
                or CustomerErrors.InvalidStatusTransition
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                title: "Adding the member failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

/// <param name="OfficeRole">Defaults to Member when omitted.</param>
/// <param name="ExpectedVersion">xmin token from GET client-groups/{groupId}; omit to skip the check.</param>
public sealed record AddGroupMemberRequest(
    Guid ClientId,
    GroupOfficeRole? OfficeRole = null,
    uint? ExpectedVersion = null);
