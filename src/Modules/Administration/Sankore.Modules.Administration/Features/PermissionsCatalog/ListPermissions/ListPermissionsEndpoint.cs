using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;

internal static class ListPermissionsEndpoint
{
    internal static IEndpointRouteBuilder MapListPermissions(this IEndpointRouteBuilder app)
    {
        app.MapGet("", async (ISender sender, CancellationToken ct, string? module = null) =>
        {
            var result = await sender.Send(new ListPermissionsQuery(module), ct);
            return Results.Ok(result.Value);
        })
        .WithName("ListPermissions")
        .WithSummary("List all application permissions grouped by module")
        .RequireAuthorization(Permissions.CanReadRole.Code)
        .Produces<List<PermissionGroupDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .WithOpenApi()
        .WithTenantHeader();

        return app;
    }
}
