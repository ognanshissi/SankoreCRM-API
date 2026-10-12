namespace Sankore.Modules.Integration.Features.Insurance.Complaints;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// One complaint, as the register's screen sees it.
///
/// <para>
/// <b>What the customer said is NOT here.</b> <c>BodyEncrypted</c> is free text about a named
/// person's grievance; <see cref="HasBody"/> says it is on file, which is what a worklist needs.
/// The ANSWER is here in clear and deliberately so — it is the institution's own position, and a
/// controller asking what was answered must not be handed a ciphertext.
/// </para>
/// </summary>
/// <param name="AcknowledgementOverdue">
/// Computed server-side from the STORED deadline, so a screen does not re-derive it and get it
/// wrong on an escalated complaint — the same reason <c>HoldingDto.CanEdit</c> carries a server
/// answer.
/// </param>
public sealed record ComplaintDto(
    Guid Id,
    string Reference,
    Guid? ConnectionId,
    Guid CrmCustomerId,
    Guid? PolicyId,
    Guid? ClaimId,
    string Channel,
    string Category,
    string Subject,
    bool HasBody,
    string Status,
    DateTimeOffset ReceivedAt,
    Guid ReceivedBy,
    DateTimeOffset AcknowledgementDueAt,
    DateTimeOffset ResolutionDueAt,
    DateTimeOffset? AcknowledgedAt,
    Guid? AcknowledgedBy,
    DateTimeOffset? EscalatedAt,
    string? EscalatedTo,
    DateTimeOffset? ClosedAt,
    Guid? ClosedBy,
    string? Resolution,
    bool? WasUpheld,
    bool IsOpen,
    bool AcknowledgementOverdue,
    bool ResolutionOverdue)
{
    internal static ComplaintDto From(InsuranceComplaint c, DateTimeOffset now) => new(
        Id: c.Id,
        Reference: c.Reference,
        ConnectionId: c.ConnectionId,
        CrmCustomerId: c.CrmCustomerId,
        PolicyId: c.PolicyId,
        ClaimId: c.ClaimId,
        Channel: c.Channel.ToString(),
        Category: c.Category.ToString(),
        Subject: c.Subject,
        HasBody: c.BodyEncrypted is not null,
        Status: c.Status.ToString(),
        ReceivedAt: c.ReceivedAt,
        ReceivedBy: c.ReceivedBy,
        AcknowledgementDueAt: c.AcknowledgementDueAt,
        ResolutionDueAt: c.ResolutionDueAt,
        AcknowledgedAt: c.AcknowledgedAt,
        AcknowledgedBy: c.AcknowledgedBy,
        EscalatedAt: c.EscalatedAt,
        EscalatedTo: c.EscalatedTo,
        ClosedAt: c.ClosedAt,
        ClosedBy: c.ClosedBy,
        Resolution: c.Resolution,
        WasUpheld: c.WasUpheld,
        IsOpen: c.IsOpen,
        AcknowledgementOverdue: c.AcknowledgementOverdueAt(now),
        ResolutionOverdue: c.ResolutionOverdueAt(now));
}

/// <summary>One complaint by its id.</summary>
internal sealed record GetComplaintQuery(Guid ComplaintId) : IRequest<Result<ComplaintDto>>;

internal sealed class GetComplaintHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<GetComplaintQuery, Result<ComplaintDto>>
{
    public async Task<Result<ComplaintDto>> Handle(GetComplaintQuery query, CancellationToken ct)
    {
        var complaint = await db.InsuranceComplaints
            .FirstOrDefaultAsync(c => c.Id == query.ComplaintId, ct);

        return complaint is null
            ? Result.Fail<ComplaintDto>(ComplaintErrors.NotFound)
            : Result.Ok(ComplaintDto.From(complaint, clock.GetUtcNow()));
    }
}

/// <summary>
/// The register: a customer's complaints, or the open ones, or the overdue ones.
///
/// <para>
/// <c>overdueOnly</c> is evaluated in MEMORY over the open set, not pushed into SQL. The reason is
/// that "overdue" is two conditions on two different deadlines and the aggregate owns both —
/// <c>AcknowledgementOverdueAt</c> also depends on whether an acknowledgement exists — and a SQL
/// predicate that re-expressed them would be a second definition. The open set is bounded by the
/// register's own worklist index, so the cost is the open complaints of one tenant, which is tens.
/// </para>
/// </summary>
internal sealed record ListComplaintsQuery(
    Guid? CrmCustomerId, bool OpenOnly, bool OverdueOnly)
    : IRequest<Result<IReadOnlyList<ComplaintDto>>>;

internal sealed class ListComplaintsHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<ListComplaintsQuery, Result<IReadOnlyList<ComplaintDto>>>
{
    /// <summary>A ceiling, not a pager — the same choice the claims and statements lists make.</summary>
    private const int MaxRows = 500;

    public async Task<Result<IReadOnlyList<ComplaintDto>>> Handle(
        ListComplaintsQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = db.InsuranceComplaints.AsQueryable();

        if (query.CrmCustomerId is { } customerId)
            rows = rows.Where(c => c.CrmCustomerId == customerId);

        // The aggregate's IsOpen expressed in SQL, because the worklist index serves it. Forced on
        // for an overdue search: a closed complaint cannot be late any more.
        if (query.OpenOnly || query.OverdueOnly)
        {
            rows = rows.Where(c => c.Status == ComplaintStatus.Received
                                || c.Status == ComplaintStatus.Acknowledged
                                || c.Status == ComplaintStatus.Escalated);
        }

        var complaints = await rows
            .OrderByDescending(c => c.ReceivedAt)
            .Take(MaxRows)
            .ToListAsync(ct);

        var mapped = complaints.Select(c => ComplaintDto.From(c, now));

        if (query.OverdueOnly)
            mapped = mapped.Where(c => c.AcknowledgementOverdue || c.ResolutionOverdue);

        return Result.Ok<IReadOnlyList<ComplaintDto>>([.. mapped]);
    }
}

/// <summary>
/// The mechanism's own figures, for the report the 2024 règlement makes the institution able to
/// produce: how many arrived, through which channel, about what, how many were upheld, and how
/// many deadlines were missed.
///
/// <para>
/// Counted over a window rather than for all time, because that is the shape of the question a
/// supervisor asks — « combien de réclamations ce trimestre ».
/// </para>
/// </summary>
internal sealed record ComplaintReportQuery(DateOnly From, DateOnly To)
    : IRequest<Result<ComplaintReportDto>>;

/// <param name="UpheldCount">
/// Of the ANSWERED ones. A withdrawal is not an answer, which is why <c>Closed</c> is a status of
/// its own — counting one as treated would overstate the mechanism.
/// </param>
/// <param name="AcknowledgementBreaches">
/// Complaints acknowledged after their deadline, or still not acknowledged past it. Both, because
/// a mechanism that only counted the first would improve by never acknowledging at all.
/// </param>
public sealed record ComplaintReportDto(
    DateOnly From,
    DateOnly To,
    int ReceivedCount,
    int OpenCount,
    int ResolvedCount,
    int WithdrawnCount,
    int UpheldCount,
    int AcknowledgementBreaches,
    int ResolutionBreaches,
    double? MedianDaysToResolve,
    IReadOnlyDictionary<string, int> ByChannel,
    IReadOnlyDictionary<string, int> ByCategory);

internal sealed class ComplaintReportHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<ComplaintReportQuery, Result<ComplaintReportDto>>
{
    public async Task<Result<ComplaintReportDto>> Handle(
        ComplaintReportQuery query, CancellationToken ct)
    {
        if (query.To < query.From)
            return Result.Fail<ComplaintReportDto>("INSURANCE_COMPLAINT_WINDOW_INVERTED");

        var now = clock.GetUtcNow();

        var from = new DateTimeOffset(query.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var complaints = await db.InsuranceComplaints
            .Where(c => c.ReceivedAt >= from && c.ReceivedAt < to)
            .ToListAsync(ct);

        var resolved = complaints.Where(c => c.Status == ComplaintStatus.Resolved).ToList();

        // Median and not mean, deliberately: one complaint that took two years to answer would
        // drag a mean past every honest figure, and the question is how long a complaint usually
        // takes.
        var durations = resolved
            .Where(c => c.ClosedAt is not null)
            .Select(c => (c.ClosedAt!.Value - c.ReceivedAt).TotalDays)
            .OrderBy(d => d)
            .ToList();

        double? median = durations.Count == 0
            ? null
            : durations.Count % 2 == 1
                ? durations[durations.Count / 2]
                : (durations[(durations.Count / 2) - 1] + durations[durations.Count / 2]) / 2;

        return Result.Ok(new ComplaintReportDto(
            From: query.From,
            To: query.To,
            ReceivedCount: complaints.Count,
            OpenCount: complaints.Count(c => c.IsOpen),
            ResolvedCount: resolved.Count,
            WithdrawnCount: complaints.Count(c => c.Status == ComplaintStatus.Closed),
            UpheldCount: resolved.Count(c => c.WasUpheld == true),
            // Late, or never done and already past the date. Both, so a mechanism cannot improve
            // its figures by not acknowledging.
            AcknowledgementBreaches: complaints.Count(
                c => (c.AcknowledgedAt is { } at && at > c.AcknowledgementDueAt)
                  || c.AcknowledgementOverdueAt(now)),
            ResolutionBreaches: complaints.Count(
                c => (c.ClosedAt is { } at && at > c.ResolutionDueAt)
                  || c.ResolutionOverdueAt(now)),
            MedianDaysToResolve: median is { } m ? Math.Round(m, 1) : null,
            ByChannel: complaints
                .GroupBy(c => c.Channel.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            ByCategory: complaints
                .GroupBy(c => c.Category.ToString())
                .ToDictionary(g => g.Key, g => g.Count())));
    }
}
