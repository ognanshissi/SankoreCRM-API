namespace Sankore.Modules.Integration.Features.Commands.ListCommands;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListIntegrationCommandsEndpoint
{
    internal static IEndpointRouteBuilder MapListIntegrationCommands(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListIntegrationCommands")
            .WithSummary("List integration commands — the rejection queue")
            .WithDescription(
                "Server-side paging, oldest first, filterable by status, error family, command "
                + "type, CRM entity, connection and a period on creation. Rows carry the error "
                + "code and detail but never the payload. An unknown filter value is refused by "
                + "name rather than ignored. Requires Integration.Command.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationCommand.Code)
            .Produces<IntegrationCommandListPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] string? status,
        [FromQuery] string? errorFamily,
        [FromQuery] string? commandType,
        [FromQuery] string? entityType,
        [FromQuery] Guid? crmId,
        [FromQuery] Guid? connectionId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ISender sender,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await sender.Send(new ListIntegrationCommandsQuery(
            status, errorFamily, commandType, entityType, crmId, connectionId,
            from, to, page, pageSize), ct);

        // 400 and never 404: an empty queue is an empty page, which is a success. Only an
        // unparsable filter fails, and the message names the accepted values.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
