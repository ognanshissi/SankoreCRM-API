namespace Sankore.Modules.Integration.Features.Connections.GetConnection;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetConnectionEndpoint
{
    internal static IEndpointRouteBuilder MapGetConnection(this IEndpointRouteBuilder app)
    {
        app.MapGet("{connectionId:guid}", Handle)
            .WithName("GetIntegrationConnection")
            .WithSummary("One connection and its last health check")
            .WithDescription(
                "The connection's coordinates, its settings object (carrying \"$kind\"), and the "
                + "outcome of its last health check. lastHealthStatus is null until a check has "
                + "run — false means a check FAILED, which is a different operational fact. No "
                + "credential is returned: the settings hold vault references, never values. A "
                + "connection of another tenant answers 404, never 403 — the existence of a "
                + "connection must not leak. "
                + "Requires permission: Integration.Connection.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationConnection.Code)
            .Produces<ConnectionDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetConnectionQuery(connectionId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
