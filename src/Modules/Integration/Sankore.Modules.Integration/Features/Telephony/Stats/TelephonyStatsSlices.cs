namespace Sankore.Modules.Integration.Features.Telephony.Stats;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// The indicators, read from the projection and never from the journal (TEL-B-19).
/// </summary>
/// <param name="From">First day, inclusive. UTC days — see <c>TelCallDailyStat.StatDate</c>.</param>
/// <param name="To">Last day, inclusive.</param>
/// <param name="AgencyId">
/// One agency, or every agency inside the caller's perimeter. An agency outside it answers an
/// empty period rather than a refusal: the caller learns nothing about whether it exists.
/// </param>
/// <param name="CrmUserId">One agent, or every agent of the agencies in scope.</param>
internal sealed record GetTelephonyStatsQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? AgencyId = null,
    Guid? CrmUserId = null)
    : IRequest<Result<TelephonyStatsDto>>;

/// <param name="CrmUserId">
/// Null for the "nobody" bucket — calls from a provider account no link resolves. Never
/// <c>Guid.Empty</c>: the sentinel the table stores does not leave this module.
/// </param>
/// <param name="AgencyId">Null for an agent who is in no agency, or a call opened without one.</param>
internal sealed record TelephonyStatsRowDto(
    Guid? CrmUserId,
    Guid? AgencyId,
    int InboundCount,
    int OutboundCount,
    int AnsweredCount,
    int MissedCount,
    int FailedCount,
    int ContactCount,
    int WrappedUpCount,
    int TalkSeconds,
    int RingSeconds);

/// <param name="Days">
/// One row per day per bucket, for a chart. Empty days are absent rather than zero-filled — the
/// client knows the range it asked for and a Sunday with no calls is not data.
/// </param>
/// <param name="ComputedAt">
/// When the freshest row in the answer was derived, so a screen can say how old the figures are
/// instead of implying they are live. Null when the period is empty.
/// </param>
internal sealed record TelephonyStatsDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<TelephonyStatsRowDto> Totals,
    IReadOnlyList<TelephonyStatsDayDto> Days,
    DateTimeOffset? ComputedAt);

internal sealed record TelephonyStatsDayDto(
    DateOnly StatDate,
    Guid? CrmUserId,
    Guid? AgencyId,
    int InboundCount,
    int OutboundCount,
    int AnsweredCount,
    int MissedCount,
    int FailedCount,
    int ContactCount,
    int WrappedUpCount,
    int TalkSeconds,
    int RingSeconds);

internal sealed class GetTelephonyStatsHandler(
    IntegrationDbContext db,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser)
    : IRequestHandler<GetTelephonyStatsQuery, Result<TelephonyStatsDto>>
{
    /// <summary>
    /// Thirty-one days by default — a month, which is what the screen opens on.
    /// </summary>
    private const int DefaultDays = 31;

    /// <summary>
    /// The ceiling on one request. The same number the recomputation refuses above, so the two
    /// halves of this feature cannot disagree about what a reasonable period is.
    /// </summary>
    private const int MaxDays = TelCallStatsRecomputer.MaxDays;

    public async Task<Result<TelephonyStatsDto>> Handle(
        GetTelephonyStatsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-(DefaultDays - 1));

        if (to < from) (from, to) = (to, from);

        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
            return Result.Fail<TelephonyStatsDto>(TelephonyErrors.StatsPeriodTooLong);

        // Null means UNRESTRICTED — a super-user or a tenant-wide account. The contract is
        // explicit that it must be read as "apply no agency filter", never as "no access", and
        // getting that backwards here would hide every figure from the one person whose job is to
        // read them.
        var scope = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        // No tenant predicate: the global query filter scopes it.
        var rows = db.TelCallDailyStats.Where(s => s.StatDate >= from && s.StatDate <= to);

        if (scope is not null)
        {
            // The unattributed bucket is deliberately NOT visible to a scoped caller. A call with
            // no agency is one this module could not place, and showing it inside a branch's
            // figures would attribute somebody else's work to that branch; showing it to everybody
            // would mean every branch manager counting the same calls. It belongs to whoever sees
            // the tenant whole.
            var visible = scope.ToList();

            rows = rows.Where(s => visible.Contains(s.AgencyId));
        }

        if (query.AgencyId is { } agencyId)
        {
            // Asked for an agency outside the perimeter: the predicate above has already removed
            // it, so this narrows to nothing and the answer is an empty period. Not a 403 — a
            // refusal would confirm the agency exists.
            rows = rows.Where(s => s.AgencyId == agencyId);
        }

        if (query.CrmUserId is { } crmUserId)
            rows = rows.Where(s => s.CrmUserId == crmUserId);

        var days = await rows
            .OrderBy(s => s.StatDate)
            .Select(s => new TelephonyStatsDayDto(
                s.StatDate,
                s.CrmUserId == TelCallDailyStat.Unattributed ? null : s.CrmUserId,
                s.AgencyId == TelCallDailyStat.Unattributed ? null : s.AgencyId,
                s.InboundCount,
                s.OutboundCount,
                s.AnsweredCount,
                s.MissedCount,
                s.FailedCount,
                s.ContactCount,
                s.WrappedUpCount,
                s.TalkSeconds,
                s.RingSeconds))
            .ToListAsync(ct);

        var computedAt = days.Count == 0
            ? (DateTimeOffset?)null
            : await rows.MaxAsync(s => (DateTimeOffset?)s.ComputedAt, ct);

        // Summed in memory over the rows already fetched rather than by a second GROUP BY: the
        // period is capped, so this is at most a few thousand rows, and one query that cannot
        // disagree with the other is worth more than one round trip.
        var totals = days
            .GroupBy(d => (d.CrmUserId, d.AgencyId))
            .Select(g => new TelephonyStatsRowDto(
                g.Key.CrmUserId,
                g.Key.AgencyId,
                g.Sum(d => d.InboundCount),
                g.Sum(d => d.OutboundCount),
                g.Sum(d => d.AnsweredCount),
                g.Sum(d => d.MissedCount),
                g.Sum(d => d.FailedCount),
                g.Sum(d => d.ContactCount),
                g.Sum(d => d.WrappedUpCount),
                g.Sum(d => d.TalkSeconds),
                g.Sum(d => d.RingSeconds)))
            .OrderByDescending(r => r.InboundCount + r.OutboundCount)
            .ToList();

        return Result.Ok(new TelephonyStatsDto(from, to, totals, days, computedAt));
    }
}

/// <summary>
/// Recomputes a period on demand (TEL-B-19).
///
/// <para>
/// <b>It exists because "recalculable" is worth nothing if only a schedule can exercise it.</b>
/// An institution that has just corrected a mis-configured outcome, re-attributed a month of
/// calls, or restored a backup needs the figures to agree with the journal today — not after the
/// nightly window happens to reach back far enough, which for anything older than a week it never
/// does.
/// </para>
/// </summary>
internal sealed record RecomputeTelephonyStatsCommand(DateOnly From, DateOnly To)
    : IRequest<Result<int>>, ICommand;

internal sealed class RecomputeTelephonyStatsHandler(
    IntegrationDbContext db,
    TimeProvider clock,
    ICurrentUser currentUser,
    ILogger<RecomputeTelephonyStatsHandler> logger)
    : IRequestHandler<RecomputeTelephonyStatsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(
        RecomputeTelephonyStatsCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var report = await TelCallStatsRecomputer.RecomputeRangeAsync(
            db, clock, logger, currentUser.TenantId, cmd.From, cmd.To, ct);

        if (report.Refused)
            return Result.Fail<int>(TelephonyErrors.StatsPeriodTooLong);

        logger.LogInformation(
            "Telephony stats recomputed by {UserId} for tenant {TenantId}: {Days} day(s), "
            + "{Buckets} bucket(s).",
            currentUser.Id, currentUser.TenantId, report.Days, report.Buckets);

        return Result.Ok(report.Buckets);
    }
}

internal static class TelephonyStatsEndpoints
{
    internal static IEndpointRouteBuilder MapTelephonyStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("stats", GetAsync)
            .WithName("GetTelephonyStats")
            .WithSummary("Call indicators by day, agent and agency")
            .WithDescription(
                "Read from a projection recomputed from the journal, not from the journal itself "
                + "— so computedAt tells you how old the figures are. Internal calls (colleague "
                + "to colleague) are excluded by design: counted in, an agent spending the "
                + "morning consulting their manager would outrank one spending it on clients. "
                + "Days are UTC days. Empty days are absent rather than zero-filled. "
                + "crmUserId or agencyId null is the 'nobody' bucket — a call from a provider "
                + "account no link resolves — and it is visible only to a caller who sees the "
                + "whole tenant, because showing it inside one branch's figures would attribute "
                + "another branch's work to it. Asking for an agency outside your perimeter "
                + "answers an empty period, not a refusal. Defaults to the last 31 days; 400 is "
                + "the ceiling. "
                + "Requires permission: Telephony.Stats.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyStats.Code)
            .Produces<TelephonyStatsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("stats/recompute", RecomputeAsync)
            .WithName("RecomputeTelephonyStats")
            .WithSummary("Derive the indicators of a period again from the journal")
            .WithDescription(
                "Replaces every bucket of the period with figures counted from tel_call. "
                + "Idempotent: running it twice gives the same rows, because nothing is "
                + "incremented. Use it after correcting an outcome's 'counts as contact' flag, "
                + "re-attributing calls, or restoring a backup — the nightly schedule only "
                + "reaches back seven days. 400 days is the ceiling. "
                + "Requires permission: Telephony.Settings.Manage.")
            // Settings.Manage and not Stats.View: this is maintenance, and a period of four
            // hundred days is expensive enough that everybody who may READ the figures should not
            // be able to launch it.
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces<int>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> GetAsync(
        ISender sender,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? agencyId = null,
        Guid? crmUserId = null)
    {
        var result = await sender.Send(
            new GetTelephonyStatsQuery(from, to, agencyId, crmUserId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> RecomputeAsync(
        ISender sender, RecomputeTelephonyStatsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new RecomputeTelephonyStatsCommand(request.From, request.To), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}

/// <param name="From">First day, inclusive, UTC.</param>
/// <param name="To">Last day, inclusive, UTC.</param>
internal sealed record RecomputeTelephonyStatsRequest(DateOnly From, DateOnly To);
