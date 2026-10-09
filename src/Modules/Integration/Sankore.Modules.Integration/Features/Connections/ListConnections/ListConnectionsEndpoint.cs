namespace Sankore.Modules.Integration.Features.Connections.ListConnections;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListConnectionsEndpoint
{
    internal static IEndpointRouteBuilder MapListConnections(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListIntegrationConnections")
            .WithSummary("List the external systems this tenant integrates with")
            .WithDescription(
                "The current tenant's connections, active ones first. Filter by family "
                + "(CoreBanking | Insurance) and by activity. The settings object is NOT in the "
                + "list — ask for one connection to get it — and no credential appears in either: "
                + "the values live in the vault and only a masked hint is ever readable, through "
                + "GET connections/{connectionId}/secrets. "
                + "Requires permission: Integration.Connection.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationConnection.Code)
            .Produces<PagedResult<ConnectionListDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        IntegrationFamily? family = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = 50)
        => Results.Ok((await sender.Send(
            new ListConnectionsQuery(family, isActive, page, pageSize), ct)).Value);
}
