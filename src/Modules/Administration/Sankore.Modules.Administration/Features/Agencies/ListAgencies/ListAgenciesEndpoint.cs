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
                "Returns all non-deleted agencies. " +
                "Pass parentId=<guid> to filter children; parentId=00000000-0000-0000-0000-000000000000 returns root-level agencies. " +
                "Pass includeDeleted=true to include soft-deleted entries. " +
                "Requires permission: agency:read.")
            .RequireAuthorization(Permissions.CanReadAgency.Code)
            .Produces<List<AgencyDto>>(StatusCodes.Status200OK)
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
        CancellationToken ct)
    {
        var result = await sender.Send(new ListAgenciesQuery(parentId, includeDeleted), ct);
        return Results.Ok(result.Value);
    }
}
