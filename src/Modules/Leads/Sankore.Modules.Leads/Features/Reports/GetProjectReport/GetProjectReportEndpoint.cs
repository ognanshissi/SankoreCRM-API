using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Reports.GetProjectReport;

public static class GetProjectReportEndpoint
{
    public static IEndpointRouteBuilder MapGetProjectReport(this IEndpointRouteBuilder app)
    {
        app.MapGet("reports/projects", Handle)
            .WithName("GetProjectReport")
            .WithTags("Lead Reports")
            .WithSummary("The « Projets » tab of the reports: KPIs against the previous period, pipeline, products, loss reasons, steps, overdue projects")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<ProjectReportDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        app.MapGet("reports/projects/export", ExportAsync)
            .WithName("ExportProjectReport")
            .WithTags("Lead Reports")
            .WithSummary("The « Projets » tab of the reports as an Excel workbook")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, LeadReportWorkbook.ContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }

    /// <param name="from">Inclusive. Defaults to 30 days before <paramref name="to"/>.</param>
    /// <param name="to">Exclusive. Defaults to now.</param>
    private static async Task<IResult> Handle(
        ISender sender, TimeProvider clock, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? agencyId = null)
    {
        var end = to ?? clock.GetUtcNow();
        var result = await sender.Send(new GetProjectReportQuery(from ?? end.AddDays(-30), end, agencyId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Refusal(result.Error);
    }

    private static async Task<IResult> ExportAsync(
        ISender sender, TimeProvider clock, IAdministrationModule admin, ITenantContext tenant, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? agencyId = null)
    {
        var now = clock.GetUtcNow();
        var end = to ?? now;
        var start = from ?? end.AddDays(-30);

        var result = await sender.Send(new GetProjectReportQuery(start, end, agencyId), ct);
        if (!result.IsSuccess)
            return Refusal(result.Error);

        var agencyName = agencyId is { } id ? (await admin.GetAgencyAsync(tenant.CurrentTenantId, id, ct))?.Name : null;
        var bytes = ProjectReportWorkbook.Build(result.Value!, agencyName, now);
        var fileName = $"rapport-projets-{start.UtcDateTime:yyyy-MM-dd}-au-{end.AddTicks(-1).UtcDateTime:yyyy-MM-dd}.xlsx";
        return Results.File(bytes, LeadReportWorkbook.ContentType, fileName);
    }

    private static IResult Refusal(string? error) => Results.Problem(
        error == "REPORT_RANGE_TOO_LONG"
            ? $"La période ne peut pas dépasser {GetLeadReportHandler.MaxRangeDays} jours."
            : "La fin de la période doit suivre son début.",
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["code"] = error });
}
