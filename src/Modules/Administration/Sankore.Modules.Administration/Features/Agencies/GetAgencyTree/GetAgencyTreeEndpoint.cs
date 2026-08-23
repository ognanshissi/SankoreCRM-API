using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;

public static class GetAgencyTreeEndpoint
{
    public static IEndpointRouteBuilder MapGetAgencyTree(this IEndpointRouteBuilder app)
    {
        app.MapGet("tree", Handle)
            .WithName("GetAgencyTree")
            .WithSummary("Get the full agency tree for the current tenant")
            .WithDescription(
                "Returns all agencies as a nested tree. " +
                "Pass rootId to get a specific subtree. " +
                "Pass includeDeleted=true to include soft-deleted nodes. " +
                "Requires permission: agency:read.")
            .RequireAuthorization(Permissions.CanReadAgency.Code)
            .Produces<List<AgencyTreeNodeDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        Guid? rootId,
        bool includeDeleted,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetAgencyTreeQuery(rootId, includeDeleted), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
