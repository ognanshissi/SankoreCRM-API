using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ListAgencies;

public static class ListAgenciesEndpoint
{
    public static IEndpointRouteBuilder MapListAgencies(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListAgencies")
            .WithSummary("List agencies for the current tenant")
            .WithDescription(
                "Returns a paginated list of agencies. " +
                "Pass parentId=<guid> to filter children; parentId=00000000-0000-0000-0000-000000000000 returns root-level agencies. " +
                "Pass includeDeleted=true to include soft-deleted entries. " +
                "Pass page and pageSize for pagination (pageSize=0 returns all). " +
                "Requires permission: agency:read.")
            .RequireAuthorization(Permissions.CanReadAgency.Code)
            .Produces<PagedResult<AgencyDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        Guid? parentId,
        bool includeDeleted,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var effectivePage = page < 1 ? 1 : page;
        var effectivePageSize = pageSize < 0 ? 20 : pageSize;

        var result = await sender.Send(
            new ListAgenciesQuery(parentId, includeDeleted, effectivePage, effectivePageSize), ct);

        return Results.Ok(result.Value);
    }
}
