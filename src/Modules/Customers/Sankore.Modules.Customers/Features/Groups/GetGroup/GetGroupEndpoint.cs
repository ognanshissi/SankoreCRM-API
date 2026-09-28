namespace Sankore.Modules.Customers.Features.Groups.GetGroup;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetGroupEndpoint
{
    public static IEndpointRouteBuilder MapGetGroup(this IEndpointRouteBuilder app)
    {
        app.MapGet("{groupId:guid}", Handle)
            .WithName("GetClientGroup")
            .WithSummary("Get one client group with its members and office roles")
            .WithDescription(
                "Returns the group, its active members and the holders of the three office roles, " +
                "plus the tenant size envelope (minimumSize / maximumSize) and the xmin version to echo " +
                "back as expectedVersion on mutating calls. " +
                "Pass includeFormerMembers=true to also list closed memberships (LeftAt / LeaveReason). " +
                "A group outside the caller's agency perimeter returns 404 GROUP_NOT_FOUND, never 403, " +
                "so its existence is not disclosed. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<GroupDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid groupId,
        ISender sender,
        bool? includeFormerMembers,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new GetGroupQuery(groupId, includeFormerMembers ?? false), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error == CustomerErrors.GroupNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.Problem(
                title: "Group lookup failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest);
    }
}
