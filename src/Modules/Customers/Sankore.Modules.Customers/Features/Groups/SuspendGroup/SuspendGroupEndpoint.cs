namespace Sankore.Modules.Customers.Features.Groups.SuspendGroup;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class SuspendGroupEndpoint
{
    public static IEndpointRouteBuilder MapSuspendGroup(this IEndpointRouteBuilder app)
    {
        app.MapPost("{groupId:guid}/suspend", Handle)
            .WithName("SuspendClientGroup")
            .WithSummary("Suspend a client group")
            .WithDescription(
                "Freezes the group without touching its memberships (reversible, unlike a dissolution). " +
                "A blank motive returns 400 REASON_REQUIRED. " +
                "404: GROUP_NOT_FOUND (also when outside the caller's perimeter). " +
                "409: INVALID_STATUS_TRANSITION (already suspended or dissolved), CONCURRENCY_CONFLICT. " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<SuspendGroupResult>(StatusCodes.Status200OK)
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
        SuspendGroupRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new SuspendGroupCommand(groupId, req.Reason, req.ExpectedVersion), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNotFound => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.InvalidStatusTransition
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                title: "Suspending the group failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record SuspendGroupRequest(string Reason, uint? ExpectedVersion = null);
