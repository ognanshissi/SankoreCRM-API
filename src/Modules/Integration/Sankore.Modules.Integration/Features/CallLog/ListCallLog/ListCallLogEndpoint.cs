namespace Sankore.Modules.Integration.Features.CallLog.ListCallLog;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListCallLogEndpoint
{
    internal static IEndpointRouteBuilder MapListCallLog(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListIntegrationCallLog")
            .WithSummary("List the journal of calls made to external systems")
            .WithDescription(
                "Newest first, server-side paging, filterable by connection, command, operation, "
                + "error family and a period on the call timestamp. The period defaults to the "
                + "last 7 days when neither bound is given: the table is partitioned by month and "
                + "an unbounded query is planned across every partition that exists. The window "
                + "actually used is returned in the response. Rows carry no payload and no "
                + "personal data — operation, endpoint path, status, duration, error family and "
                + "correlation id — which is what makes the journal safe to export. A connection "
                + "or command belonging to another tenant yields an empty page, never a refusal. "
                + "Requires permission: Integration.Command.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationCommand.Code)
            .Produces<CallLogPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] Guid? connectionId,
        [FromQuery] Guid? commandId,
        [FromQuery] string? operation,
        [FromQuery] string? errorFamily,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ISender sender,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await sender.Send(
            new ListCallLogQuery(connectionId, commandId, operation, errorFamily, from, to, page, pageSize),
            ct);

        // 400 and never 404: the only way this fails is an unparsable error family, which is a
        // malformed request and the message names the accepted values. Nothing here can be "not
        // found" — an empty journal, or a connection id this tenant does not own, is an empty
        // page, which is a success.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
