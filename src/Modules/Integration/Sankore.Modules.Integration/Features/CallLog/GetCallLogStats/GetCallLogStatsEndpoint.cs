namespace Sankore.Modules.Integration.Features.CallLog.GetCallLogStats;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetCallLogStatsEndpoint
{
    internal static IEndpointRouteBuilder MapGetCallLogStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("stats", Handle)
            .WithName("GetIntegrationCallLogStats")
            .WithSummary("Per-operation call counts and p95 duration over a window")
            .WithDescription(
                "Counts, failures broken out by error family, average, nearest-rank p95 and max "
                + "duration, per logical operation, busiest first. The period defaults to the "
                + "last 7 days when neither bound is given, for the same reason as the list: the "
                + "table is partitioned by month and an unbounded aggregate reads every "
                + "partition. Pass from=this morning for 'is the back-office slow today'. An "
                + "operation with no call in the window is absent rather than reported as zero. "
                + "A connection belonging to another tenant yields an empty set of operations, "
                + "never a refusal. Requires permission: Integration.Command.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationCommand.Code)
            .Produces<CallLogStats>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] Guid? connectionId,
        [FromQuery] string? errorFamily,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        ISender sender,
        CancellationToken ct = default)
    {
        var result = await sender.Send(
            new GetCallLogStatsQuery(connectionId, errorFamily, from, to), ct);

        // 400 and never 404, same as the list: an unparsable error family is the only failure
        // mode, and an empty window is a success with no operations.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
