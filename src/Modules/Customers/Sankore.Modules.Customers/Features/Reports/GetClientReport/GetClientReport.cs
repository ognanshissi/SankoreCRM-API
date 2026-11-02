namespace Sankore.Modules.Customers.Features.Reports.GetClientReport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <param name="From">Inclusive, UTC.</param>
/// <param name="To">Exclusive, UTC.</param>
public sealed record GetClientReportQuery(DateTimeOffset From, DateTimeOffset To, Guid? AgencyId)
    : IRequest<Result<ClientReportDto>>;

/// <summary>The « Clients » tab of the reports, in one response. Aggregates only, no client named.</summary>
/// <param name="Previous">The period figures over the period of the same length just before.</param>
/// <param name="ByType">The client base today (active or awaiting KYC) and the new clients, by type.</param>
/// <param name="BySegment">The client base today by segment code; null = not segmented.</param>
/// <param name="ByAgency">New clients in the period and active clients today, by agency.</param>
public sealed record ClientReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    ClientReportKpis Kpis,
    ClientReportPeriodKpis Previous,
    IReadOnlyList<ClientReportType> ByType,
    IReadOnlyList<ClientReportSegment> BySegment,
    IReadOnlyList<ClientReportAgency> ByAgency);

/// <param name="Active">Clients whose status is Active today.</param>
/// <param name="WithoutAdvisor">Active or awaiting KYC, with no advisor today.</param>
/// <param name="AvgProductsPerClient">Active holdings ÷ active clients, today.</param>
/// <param name="HighRisk">Active or awaiting KYC, rated high risk.</param>
/// <param name="Pep">Active or awaiting KYC, with a politically exposed person among them or their household.</param>
/// <param name="OpenComplaints">Complaints not yet answered today.</param>
/// <param name="OverdueComplaints">Open complaints past their resolution deadline.</param>
public sealed record ClientReportKpis(
    ClientReportPeriodKpis Period,
    int Active,
    int WithoutAdvisor,
    double? AvgProductsPerClient,
    int HighRisk,
    int Pep,
    int OpenComplaints,
    int OverdueComplaints);

/// <param name="NewClients">Clients created in the period.</param>
/// <param name="ComplaintsReceived">Complaints received in the period.</param>
/// <param name="ComplaintsResolved">Complaints answered in the period.</param>
/// <param name="AvgResolutionDays">Average days from reception to answer, for those answered in the period.</param>
/// <param name="SuspendedOrArchived">Clients moved to Suspended or Archived in the period.</param>
public sealed record ClientReportPeriodKpis(
    int NewClients,
    int ComplaintsReceived,
    int ComplaintsResolved,
    double? AvgResolutionDays,
    int SuspendedOrArchived);

public sealed record ClientReportType(ClientType Type, int Base, int NewClients);

public sealed record ClientReportSegment(string? SegmentCode, int Count);

public sealed record ClientReportAgency(Guid AgencyId, string Name, int NewClients, int Active);

/// <summary>
/// The « Clients » tab of the reports (reports lot 3, 2026-11-02). Same rule as the lead and project
/// reports: the whole tenant under <c>lead:report:view</c>, the one permission of the reports page,
/// optionally narrowed to one agency. Merged and anonymised records are not clients any more and are
/// left out of every count.
/// </summary>
internal sealed class GetClientReportHandler(
    CustomersDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    TimeProvider clock)
    : IRequestHandler<GetClientReportQuery, Result<ClientReportDto>>
{
    /// <summary>Same ceiling as the lead report: a period longer than this is refused.</summary>
    public const int MaxRangeDays = 400;

    public async Task<Result<ClientReportDto>> Handle(GetClientReportQuery q, CancellationToken ct)
    {
        if (q.To <= q.From)
            return Result.Fail<ClientReportDto>("REPORT_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > MaxRangeDays)
            return Result.Fail<ClientReportDto>("REPORT_RANGE_TOO_LONG");

        var now = clock.GetUtcNow();
        var previousFrom = q.From - (q.To - q.From);

        var clients = db.Clients.AsNoTracking().Where(c => c.Status != ClientStatus.Merged && !c.IsAnonymized);
        if (q.AgencyId is { } agency)
            clients = clients.Where(c => c.AgencyId == agency);

        // The base: what an institution calls its clients today — active, or opened and awaiting KYC.
        var baseRows = await clients
            .Where(c => c.Status == ClientStatus.Active || c.Status == ClientStatus.PendingKyc)
            .Select(c => new { c.Id, c.Type, c.Status, c.AgencyId, c.AdvisorUserId, c.RiskLevel, c.HasPepExposure, c.SegmentCode })
            .ToListAsync(ct);

        var newRows = await clients
            .Where(c => c.CreatedAt >= previousFrom && c.CreatedAt < q.To)
            .Select(c => new { c.Type, c.AgencyId, c.CreatedAt })
            .ToListAsync(ct);

        var activeIds = baseRows.Where(r => r.Status == ClientStatus.Active).Select(r => r.Id).ToList();
        var activeHoldings = activeIds.Count == 0
            ? 0
            : await db.ProductHoldings.AsNoTracking()
                .CountAsync(h => h.Status == HoldingStatus.Active && activeIds.Contains(h.ClientId), ct);

        var complaints = db.ClientComplaints.AsNoTracking().AsQueryable();
        if (q.AgencyId is { } complaintAgency)
            complaints = complaints.Where(c => c.AgencyId == complaintAgency);
        var complaintRows = await complaints
            .Where(c => c.ReceivedAt >= previousFrom
                        || (c.ResolvedAt != null && c.ResolvedAt >= previousFrom)
                        || c.Status == ClientComplaintStatus.Received || c.Status == ClientComplaintStatus.Acknowledged)
            .Select(c => new { c.Status, c.ReceivedAt, c.ResolvedAt, c.ResolutionDueAt })
            .ToListAsync(ct);

        var clientIds = clients.Select(c => c.Id);
        var exits = await db.ClientStatusHistories.AsNoTracking()
            .Where(h => h.OccurredAt >= previousFrom && h.OccurredAt < q.To
                        && (h.NewStatus == ClientStatus.Suspended || h.NewStatus == ClientStatus.Archived)
                        && clientIds.Contains(h.ClientId))
            .Select(h => h.OccurredAt)
            .ToListAsync(ct);

        ClientReportPeriodKpis Period(DateTimeOffset from, DateTimeOffset to)
        {
            bool In(DateTimeOffset at) => at >= from && at < to;
            var resolved = complaintRows.Where(c => c.ResolvedAt is { } r && In(r)).ToList();
            return new ClientReportPeriodKpis(
                NewClients: newRows.Count(r => In(r.CreatedAt)),
                ComplaintsReceived: complaintRows.Count(c => In(c.ReceivedAt)),
                ComplaintsResolved: resolved.Count,
                AvgResolutionDays: resolved.Count > 0
                    ? Math.Round(resolved.Average(c => (c.ResolvedAt!.Value - c.ReceivedAt).TotalDays), 1)
                    : null,
                SuspendedOrArchived: exits.Count(In));
        }

        var open = complaintRows.Where(c => c.Status is ClientComplaintStatus.Received or ClientComplaintStatus.Acknowledged).ToList();
        var kpis = new ClientReportKpis(
            Period: Period(q.From, q.To),
            Active: activeIds.Count,
            WithoutAdvisor: baseRows.Count(r => r.AdvisorUserId == null),
            AvgProductsPerClient: activeIds.Count > 0 ? Math.Round((double)activeHoldings / activeIds.Count, 2) : null,
            HighRisk: baseRows.Count(r => r.RiskLevel == RiskLevel.High),
            Pep: baseRows.Count(r => r.HasPepExposure),
            OpenComplaints: open.Count,
            OverdueComplaints: open.Count(c => c.ResolutionDueAt < now));

        var newInPeriod = newRows.Where(r => r.CreatedAt >= q.From).ToList();
        var byType = new[] { ClientType.Individual, ClientType.Legal }
            .Select(t => new ClientReportType(t, baseRows.Count(r => r.Type == t), newInPeriod.Count(r => r.Type == t)))
            .ToList();

        var bySegment = baseRows.GroupBy(r => r.SegmentCode)
            .Select(g => new ClientReportSegment(g.Key, g.Count()))
            .OrderBy(s => s.SegmentCode is null)
            .ThenByDescending(s => s.Count)
            .ToList();

        var agencyIds = baseRows.Select(r => r.AgencyId).Concat(newInPeriod.Select(r => r.AgencyId)).Distinct().ToList();
        var byAgency = new List<ClientReportAgency>();
        foreach (var id in agencyIds)
        {
            // One call per agency: a tenant has a handful, and the contract has no batch read.
            var name = (await admin.GetAgencyAsync(tenant.CurrentTenantId, id, ct))?.Name ?? "Agence inconnue";
            byAgency.Add(new ClientReportAgency(
                id, name, newInPeriod.Count(r => r.AgencyId == id),
                baseRows.Count(r => r.AgencyId == id && r.Status == ClientStatus.Active)));
        }

        return Result.Ok(new ClientReportDto(
            q.From, q.To, kpis, Period(previousFrom, q.From), byType, bySegment,
            byAgency.OrderByDescending(a => a.Active).ThenByDescending(a => a.NewClients).ToList()));
    }
}

public static class GetClientReportEndpoint
{
    public static IEndpointRouteBuilder MapClientReportsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("clients/reports", Handle)
            .WithName("GetClientReport")
            .WithTags("Client Reports")
            .WithSummary("The « Clients » tab of the reports: KPIs against the previous period, base by type, segment and agency")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<ClientReportDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        app.MapGet("clients/reports/export", ExportAsync)
            .WithName("ExportClientReport")
            .WithTags("Client Reports")
            .WithSummary("The « Clients » tab of the reports as an Excel workbook")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, ClientReportWorkbook.ContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();
        return app;
    }

    private static async Task<IResult> ExportAsync(
        ISender sender, TimeProvider clock, IAdministrationModule admin, ITenantContext tenant, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? agencyId = null)
    {
        var now = clock.GetUtcNow();
        var end = to ?? now;
        var start = from ?? end.AddDays(-30);
        var result = await sender.Send(new GetClientReportQuery(start, end, agencyId), ct);
        if (!result.IsSuccess) return Refusal(result.Error);

        var agencyName = agencyId is { } id ? (await admin.GetAgencyAsync(tenant.CurrentTenantId, id, ct))?.Name : null;
        var bytes = ClientReportWorkbook.Build(result.Value!, agencyName, now);
        var fileName = $"rapport-clients-{start.UtcDateTime:yyyy-MM-dd}-au-{end.AddTicks(-1).UtcDateTime:yyyy-MM-dd}.xlsx";
        return Results.File(bytes, ClientReportWorkbook.ContentType, fileName);
    }

    /// <param name="from">Inclusive. Defaults to 30 days before <paramref name="to"/>.</param>
    /// <param name="to">Exclusive. Defaults to now.</param>
    private static async Task<IResult> Handle(
        ISender sender, TimeProvider clock, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? agencyId = null)
    {
        var end = to ?? clock.GetUtcNow();
        var result = await sender.Send(new GetClientReportQuery(from ?? end.AddDays(-30), end, agencyId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Refusal(result.Error);
    }

    private static IResult Refusal(string? error) => Results.Problem(
        error == "REPORT_RANGE_TOO_LONG"
            ? $"La période ne peut pas dépasser {GetClientReportHandler.MaxRangeDays} jours."
            : "La fin de la période doit suivre son début.",
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["code"] = error });
}
