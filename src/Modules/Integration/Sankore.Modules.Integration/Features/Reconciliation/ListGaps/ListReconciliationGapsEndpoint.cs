namespace Sankore.Modules.Integration.Features.Reconciliation.ListGaps;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListReconciliationGapsEndpoint
{
    internal static IEndpointRouteBuilder MapListReconciliationGaps(this IEndpointRouteBuilder app)
    {
        app.MapGet("gaps", Handle)
            .WithName("ListReconciliationGaps")
            .WithSummary("List the gaps found by the daily CRM / external-system comparison")
            .WithDescription(
                "Oldest divergence first — the figure that matters about a finding is how long it "
                + "has been open — with server-side paging and filters on connection, gap type "
                + "and resolution. Resolution defaults to Open: the question is what is "
                + "outstanding. The response also carries the open count per type for the whole "
                + "scope (not just the page), the gap types this deployment cannot detect at all "
                + "so a zero is never mistaken for a measurement, and the last comparison's own "
                + "outcome — including a failed one, which is itself a finding. Rows carry "
                + "compared values only (a status, a KYC tier), never names or contact details, "
                + "which is what makes the report safe to export. A connection belonging to "
                + "another tenant yields an empty page, never a refusal. Requires permission: "
                + "Integration.Reconciliation.View.")
            .RequireAuthorization(Permissions.CanViewIntegrationReconciliation.Code)
            .Produces<ReconciliationGapPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        [FromQuery] Guid? connectionId,
        [FromQuery] string? gapType,
        [FromQuery] string? resolution,
        ISender sender,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await sender.Send(
            new ListReconciliationGapsQuery(connectionId, gapType, resolution, page, pageSize),
            ct);

        // 400 and never 404: the only way this fails is an unparsable gap type or resolution,
        // which is a malformed request. Nothing here can be "not found" — an empty ledger, or a
        // connection id this tenant does not own, is an empty page, which is a success.
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
