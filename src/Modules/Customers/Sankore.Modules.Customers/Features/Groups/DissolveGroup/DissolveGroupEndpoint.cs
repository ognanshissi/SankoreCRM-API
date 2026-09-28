namespace Sankore.Modules.Customers.Features.Groups.DissolveGroup;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DissolveGroupEndpoint
{
    public static IEndpointRouteBuilder MapDissolveGroup(this IEndpointRouteBuilder app)
    {
        app.MapPost("{groupId:guid}/dissolve", Handle)
            .WithName("DissolveClientGroup")
            .WithSummary("Dissolve a client group")
            .WithDescription(
                "Terminal operation: the group becomes Dissolved and every still-open membership is closed " +
                "with the same motive (closed, never deleted), then GroupDissolvedEvent is published. " +
                "A blank motive returns 400 REASON_REQUIRED. " +
                "404: GROUP_NOT_FOUND (also when outside the caller's perimeter). " +
                "409: INVALID_STATUS_TRANSITION (already dissolved), CONCURRENCY_CONFLICT. " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<DissolveGroupResult>(StatusCodes.Status200OK)
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
        DissolveGroupRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new DissolveGroupCommand(groupId, req.Reason, req.ExpectedVersion), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNotFound => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.InvalidStatusTransition
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                title: "Dissolving the group failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record DissolveGroupRequest(string Reason, uint? ExpectedVersion = null);
