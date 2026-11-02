namespace Sankore.Modules.Kyc.Features.Reports;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

/// <param name="From">Inclusive, UTC.</param>
/// <param name="To">Exclusive, UTC.</param>
public sealed record GetKycReportQuery(DateTimeOffset From, DateTimeOffset To, Guid? AgencyId)
    : IRequest<Result<KycReportDto>>;

/// <summary>The « KYC » tab of the reports, in one response.</summary>
/// <param name="Previous">The period figures over the period of the same length just before.</param>
/// <param name="Waiting">Files in validation today, by the approval level they wait for.</param>
/// <param name="ByTier">Files validated in the period, by tier.</param>
/// <param name="ByChannel">Files opened in the period, by channel.</param>
/// <param name="ByVigilance">Files opened in the period, by vigilance level.</param>
/// <param name="ByAgency">Files opened and validated in the period, by agency.</param>
/// <param name="Longest">The files waiting longest for a signature, at most <see cref="GetKycReportHandler.MaxLongest"/>.</param>
public sealed record KycReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    KycReportKpis Kpis,
    KycReportPeriodKpis Previous,
    IReadOnlyList<KycReportWaitingLevel> Waiting,
    IReadOnlyList<KycReportCount> ByTier,
    IReadOnlyList<KycReportCount> ByChannel,
    IReadOnlyList<KycReportCount> ByVigilance,
    IReadOnlyList<KycReportAgency> ByAgency,
    IReadOnlyList<KycReportWaitingFile> Longest);

/// <param name="InValidation">Files waiting for a signature today.</param>
/// <param name="ReviewsDueSoon">Periodic or event reviews due within <see cref="GetKycReportHandler.ReviewHorizonDays"/> days.</param>
/// <param name="ReviewsOverdue">Reviews whose due date has passed and which are not done.</param>
public sealed record KycReportKpis(KycReportPeriodKpis Period, int InValidation, int ReviewsDueSoon, int ReviewsOverdue);

/// <param name="Opened">Files opened in the period.</param>
/// <param name="Validated">Files validated in the period (simplified or full).</param>
/// <param name="Rejected">Files refused in the period, by a signed refusal.</param>
/// <param name="AvgValidationDays">Average days from opening to validation, for the files validated in the period.</param>
/// <param name="ComplementRate">Of the files validated or refused in the period, the share sent back for a complement at least once, in percent.</param>
/// <param name="FirstPassRate">Of the files validated in the period, the share never sent back, in percent.</param>
/// <param name="Automatic">Files validated in the period with no human (remote automatic approval).</param>
public sealed record KycReportPeriodKpis(
    int Opened, int Validated, int Rejected, double? AvgValidationDays,
    double? ComplementRate, double? FirstPassRate, int Automatic);

/// <param name="Level">The approval level the files wait for: Agent, BranchManager, ComplianceOfficer…</param>
public sealed record KycReportWaitingLevel(string Level, int Count, double? AvgDays, double? OldestDays);

public sealed record KycReportCount(string Key, int Count);

public sealed record KycReportAgency(Guid? AgencyId, string Name, int Opened, int Validated);

public sealed record KycReportWaitingFile(
    Guid KycFileId, string CustomerName, string Level, double WaitingDays, string? ResponsibleName, string Agency);

/// <summary>
/// The « KYC » tab of the reports (reports lot 5, 2026-11-02). Same rule as the other tabs: the
/// whole tenant under <c>lead:report:view</c>, optionally narrowed to one agency.
///
/// <para>
/// A file « waits » at the lowest pending rung of its circuit — the only one
/// <c>DecideKycApprovalHandler</c> accepts — since the previous rung was signed, or since the
/// circuit was written for the first one. A complement is read off the circuit: a step decided
/// « ComplementRequired » stays on the file.
/// </para>
/// </summary>
internal sealed class GetKycReportHandler(
    KycDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    ICustomersModule customers,
    TimeProvider clock)
    : IRequestHandler<GetKycReportQuery, Result<KycReportDto>>
{
    public const int MaxRangeDays = 400;
    public const int MaxLongest = 20;
    public const int ReviewHorizonDays = 30;

    private static readonly KycFileStatus[] ValidatedStatuses = [KycFileStatus.Simplified, KycFileStatus.Full, KycFileStatus.UnderReview];

    public async Task<Result<KycReportDto>> Handle(GetKycReportQuery q, CancellationToken ct)
    {
        if (q.To <= q.From)
            return Result.Fail<KycReportDto>("REPORT_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > MaxRangeDays)
            return Result.Fail<KycReportDto>("REPORT_RANGE_TOO_LONG");

        var now = clock.GetUtcNow();
        var previousFrom = q.From - (q.To - q.From);

        var files = db.KycFiles.AsNoTracking();
        if (q.AgencyId is { } agency)
            files = files.Where(f => f.AgencyId == agency);

        var rows = await files
            .Where(f => f.CreatedAt >= previousFrom
                        || (f.ValidatedAt != null && f.ValidatedAt >= previousFrom)
                        || f.Status == KycFileStatus.Validating)
            .Select(f => new
            {
                f.Id, f.CustomerId, f.AgencyId, f.Status, f.Tier, f.Channel, f.VigilanceLevel,
                f.CreatedAt, f.ValidatedAt, f.ValidationRoute, f.ResponsibleAgentId,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();

        var steps = await db.KycApprovalSteps.AsNoTracking()
            .Where(s => ids.Contains(s.KycFileId))
            .Select(s => new { s.KycFileId, s.Level, s.LevelRank, s.Decision, s.DecidedAt, s.CreatedAt })
            .ToListAsync(ct);
        var stepsByFile = steps.GroupBy(s => s.KycFileId).ToDictionary(g => g.Key, g => g.ToList());

        var complemented = steps.Where(s => s.Decision == KycApprovalDecision.ComplementRequired)
            .Select(s => s.KycFileId).ToHashSet();
        var rejections = steps.Where(s => s.Decision == KycApprovalDecision.Rejected && s.DecidedAt != null)
            .GroupBy(s => s.KycFileId)
            .ToDictionary(g => g.Key, g => g.Min(s => s.DecidedAt!.Value));

        KycReportPeriodKpis Period(DateTimeOffset from, DateTimeOffset to)
        {
            bool In(DateTimeOffset? at) => at is { } a && a >= from && a < to;
            var validated = rows.Where(r => In(r.ValidatedAt) && ValidatedStatuses.Contains(r.Status)).ToList();
            var rejected = rows.Where(r => rejections.TryGetValue(r.Id, out var at) && In(at)).ToList();
            var decided = validated.Select(r => r.Id).Concat(rejected.Select(r => r.Id)).Distinct().ToList();
            return new KycReportPeriodKpis(
                Opened: rows.Count(r => In(r.CreatedAt)),
                Validated: validated.Count,
                Rejected: rejected.Count,
                AvgValidationDays: validated.Count > 0
                    ? Math.Round(validated.Average(r => (r.ValidatedAt!.Value - r.CreatedAt).TotalDays), 1)
                    : null,
                ComplementRate: decided.Count > 0 ? Rate(decided.Count(complemented.Contains), decided.Count) : null,
                FirstPassRate: validated.Count > 0 ? Rate(validated.Count(r => !complemented.Contains(r.Id)), validated.Count) : null,
                Automatic: validated.Count(r => r.ValidationRoute == KycValidationRoute.Automatic));
        }

        // ── Files waiting for a signature today ──────────────────────────────────────────────
        var waitingFiles = new List<(Guid FileId, Guid CustomerId, Guid? Agency, Guid? Responsible, KycApprovalLevel Level, double Days)>();
        foreach (var r in rows.Where(r => r.Status == KycFileStatus.Validating))
        {
            var mine = stepsByFile.GetValueOrDefault(r.Id) ?? [];
            var next = mine.Where(s => s.Decision == KycApprovalDecision.Pending).OrderBy(s => s.LevelRank).FirstOrDefault();
            if (next is null) continue;
            var since = mine.Where(s => s.Decision == KycApprovalDecision.Approved && s.DecidedAt != null)
                .Select(s => s.DecidedAt!.Value)
                .Append(next.CreatedAt)
                .Max();
            waitingFiles.Add((r.Id, r.CustomerId, r.AgencyId, r.ResponsibleAgentId, next.Level, (now - since).TotalDays));
        }

        var waiting = waitingFiles.GroupBy(w => w.Level).OrderBy(g => g.Key)
            .Select(g => new KycReportWaitingLevel(
                g.Key.ToString(), g.Count(), Math.Round(g.Average(w => w.Days), 1), Math.Round(g.Max(w => w.Days), 1)))
            .ToList();

        // ── Reviews due ─────────────────────────────────────────────────────────────────────
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var horizon = today.AddDays(ReviewHorizonDays);
        var reviewFiles = files.Select(f => f.Id);
        var openReviews = await db.KycReviewSchedules.AsNoTracking()
            .Where(r => (r.Status == KycReviewStatus.Scheduled || r.Status == KycReviewStatus.Due)
                        && r.DueDate <= horizon && reviewFiles.Contains(r.KycFileId))
            .Select(r => r.DueDate)
            .ToListAsync(ct);

        var kpis = new KycReportKpis(
            Period(q.From, q.To),
            InValidation: waitingFiles.Count,
            ReviewsDueSoon: openReviews.Count(d => d >= today),
            ReviewsOverdue: openReviews.Count(d => d < today));

        // ── Breakdowns of the period ────────────────────────────────────────────────────────
        var opened = rows.Where(r => r.CreatedAt >= q.From && r.CreatedAt < q.To).ToList();
        var validatedNow = rows.Where(r => r.ValidatedAt is { } v && v >= q.From && v < q.To && ValidatedStatuses.Contains(r.Status)).ToList();

        var byTier = validatedNow.GroupBy(r => r.Tier.ToString())
            .Select(g => new KycReportCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList();
        var byChannel = opened.GroupBy(r => r.Channel.ToString())
            .Select(g => new KycReportCount(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList();
        var byVigilance = opened.GroupBy(r => r.VigilanceLevel)
            .OrderByDescending(g => g.Key)
            .Select(g => new KycReportCount(g.Key.ToString(), g.Count())).ToList();

        var agencyNames = new Dictionary<Guid, string>();
        async Task<string> AgencyName(Guid? id)
        {
            if (id is not { } a) return "Sans agence";
            if (!agencyNames.TryGetValue(a, out var name))
                agencyNames[a] = name = (await admin.GetAgencyAsync(tenant.CurrentTenantId, a, ct))?.Name ?? "Agence inconnue";
            return name;
        }

        var byAgency = new List<KycReportAgency>();
        foreach (var id in opened.Select(r => r.AgencyId).Concat(validatedNow.Select(r => r.AgencyId)).Distinct())
            byAgency.Add(new KycReportAgency(id, await AgencyName(id),
                opened.Count(r => r.AgencyId == id), validatedNow.Count(r => r.AgencyId == id)));

        // ── The files waiting longest, named: this tab is read by managers and compliance ──
        var longestRows = waitingFiles.OrderByDescending(w => w.Days).Take(MaxLongest).ToList();
        var responsibleIds = longestRows.Select(w => w.Responsible).OfType<Guid>().Distinct().ToList();
        var responsibles = responsibleIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenant.CurrentTenantId, responsibleIds, ct);
        var longest = new List<KycReportWaitingFile>();
        foreach (var w in longestRows)
        {
            var customer = await customers.GetCustomerAsync(tenant.CurrentTenantId, w.CustomerId, ct);
            longest.Add(new KycReportWaitingFile(
                w.FileId, customer?.DisplayName ?? "Client inconnu", w.Level.ToString(), Math.Round(w.Days, 1),
                w.Responsible is { } r ? responsibles.GetValueOrDefault(r) : null, await AgencyName(w.Agency)));
        }

        return Result.Ok(new KycReportDto(
            q.From, q.To, kpis, Period(previousFrom, q.From), waiting, byTier, byChannel, byVigilance,
            byAgency.OrderByDescending(a => a.Opened).ToList(), longest));
    }

    private static double Rate(int part, int total) => Math.Round((double)part / total * 100, 1);
}

public static class GetKycReportEndpoint
{
    public static IEndpointRouteBuilder MapKycReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("kyc-files/reports", Handle)
            .WithName("GetKycReport")
            .WithTags("KYC Reports")
            .WithSummary("The « KYC » tab of the reports: KPIs against the previous period, files waiting per level, tiers, channels, agencies")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<KycReportDto>()
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
        var result = await sender.Send(new GetKycReportQuery(from ?? end.AddDays(-30), end, agencyId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(
                result.Error == "REPORT_RANGE_TOO_LONG"
                    ? $"La période ne peut pas dépasser {GetKycReportHandler.MaxRangeDays} jours."
                    : "La fin de la période doit suivre son début.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error });
    }
}
