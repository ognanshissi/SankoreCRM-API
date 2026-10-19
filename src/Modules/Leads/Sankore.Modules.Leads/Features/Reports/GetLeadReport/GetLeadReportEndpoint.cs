using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Reports.GetLeadReport;

public static class GetLeadReportEndpoint
{
    public static IEndpointRouteBuilder MapGetLeadReport(this IEndpointRouteBuilder app)
    {
        app.MapGet("reports/overview", Handle)
            .WithName("GetLeadReport")
            .WithTags("Lead Reports")
            .WithSummary("Administrators' lead report: KPIs, time series, progression, sources, agents and agencies")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<LeadReportDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        // Same query, same permission, same numbers — as a workbook. A separate route rather than an
        // Accept header so a plain link can request it and the generated client gets a Blob.
        app.MapGet("reports/overview/export", ExportAsync)
            .WithName("ExportLeadReport")
            .WithTags("Lead Reports")
            .WithSummary("The administrators' lead report as an Excel workbook (one sheet per block)")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, LeadReportWorkbook.ContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ExportAsync(
        ISender sender,
        TimeProvider clock,
        IAdministrationModule admin,
        ITenantContext tenant,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? agencyId = null,
        ReportGranularity? granularity = null)
    {
        var now = clock.GetUtcNow();
        var end = to ?? now;
        var start = from ?? end.AddDays(-30);

        var result = await sender.Send(new GetLeadReportQuery(start, end, agencyId, granularity), ct);
        if (!result.IsSuccess)
            return Refusal(result.Error);

        var agencyName = agencyId is { } id
            ? (await admin.GetAgencyAsync(tenant.CurrentTenantId, id, ct))?.Name
            : null;

        var bytes = LeadReportWorkbook.Build(result.Value!, agencyName, now);
        var fileName = $"rapport-leads-{start.UtcDateTime:yyyy-MM-dd}-au-{end.AddTicks(-1).UtcDateTime:yyyy-MM-dd}.xlsx";
        return Results.File(bytes, LeadReportWorkbook.ContentType, fileName);
    }

    private static IResult Refusal(string? error) => Results.Problem(
        error == "REPORT_RANGE_TOO_LONG"
            ? $"La période ne peut pas dépasser {GetLeadReportHandler.MaxRangeDays} jours."
            : "La fin de la période doit suivre son début.",
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["code"] = error });

    /// <param name="from">Inclusive. Defaults to 30 days before <paramref name="to"/>.</param>
    /// <param name="to">Exclusive. Defaults to now.</param>
    private static async Task<IResult> Handle(
        ISender sender,
        TimeProvider clock,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? agencyId = null,
        ReportGranularity? granularity = null)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-30);

        var result = await sender.Send(new GetLeadReportQuery(start, end, agencyId, granularity), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Refusal(result.Error);
    }
}
