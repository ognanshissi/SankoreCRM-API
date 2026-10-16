namespace Sankore.Modules.Integration.Features.Telephony.Missed;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The agent's follow-up list (TEL-B-13): the calls nobody answered, grouped per caller.
/// </summary>
/// <param name="Since">
/// How far back to look. Defaulted rather than unbounded: this is a list of work to do, and a
/// query over a year of missed calls is a report — which is Lot 6's business and has its own
/// permission.
/// </param>
internal sealed record ListMissedCallsQuery(DateTimeOffset? Since = null)
    : IRequest<Result<IReadOnlyList<MissedCallDto>>>;

/// <param name="AttemptCount">
/// How many times this caller tried inside the grouping window. It is the difference between
/// somebody who rang once and somebody who has been trying all morning, and it is the only
/// field here an agent uses to decide what to do first.
/// </param>
/// <param name="EntityId">
/// Who the caller is, when the journal managed to attach them. NO name and NO number: a name
/// needs the agency perimeter, which this list does not apply — the screen reads
/// GET calls/{callId}/context for that, which does.
/// </param>
internal sealed record MissedCallDto(
    Guid CallId,
    int AttemptCount,
    DateTimeOffset FirstAttemptAt,
    DateTimeOffset LastAttemptAt,
    string? EntityType,
    Guid? EntityId,
    bool CanCreateLead);

internal sealed class ListMissedCallsHandler(IntegrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ListMissedCallsQuery, Result<IReadOnlyList<MissedCallDto>>>
{
    /// <summary>
    /// Seven days. A missed call older than that is not work an agent is going to do today, and
    /// an unbounded default would make the first page of a busy tenant a table scan.
    /// </summary>
    private static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(7);

    public async Task<Result<IReadOnlyList<MissedCallDto>>> Handle(
        ListMissedCallsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var since = query.Since ?? DateTimeOffset.UtcNow - DefaultLookback;
        var me = currentUser.Id;

        // MINE, or nobody's. The second half is the agency's "to attribute" queue — a call from an
        // unlinked provider account, which is exactly the call most likely to be forgotten — and
        // leaving it out would make the list quietly incomplete. A colleague's missed call is
        // theirs: showing it here would have two agents calling one person back.
        //
        // No tenant predicate: the global query filter scopes it.
        var rows = await db.TelCalls
            .Where(c => c.StartedAt >= since
                     && c.Direction == TelCallDirection.Inbound
                     && (c.Status == TelCallStatus.Missed || c.Status == TelCallStatus.Voicemail)
                     && (c.CrmUserId == me || c.CrmUserId == null)
                     // A call already wrapped up has been dealt with, whatever its status: an
                     // agent who called back and noted the outcome must not keep seeing it.
                     && c.WrappedUpAt == null)
            .Select(c => new
            {
                c.Id, c.StartedAt, c.EntityType, c.EntityId, c.CrmUserId, c.FromNumberBi,
            })
            .ToListAsync(ct);

        var groups = MissedCallGrouping.Group(rows.Select(r => new MissedCallAttempt(
            r.Id,
            // Hex of the blind index, or empty for a withheld caller id. Never a decrypted number:
            // the grouping compares callers and has no business reading one.
            r.FromNumberBi is { Length: > 0 } bi ? Convert.ToHexStringLower(bi) : string.Empty,
            r.StartedAt,
            r.EntityType,
            r.EntityId,
            r.CrmUserId)));

        return Result.Ok<IReadOnlyList<MissedCallDto>>(groups
            // Most recent attempt first: the list is read top-down and the freshest call is the
            // one most likely to be picked up.
            .OrderByDescending(g => g.LastAttemptAt)
            .Select(g => new MissedCallDto(
                g.RepresentativeCallId,
                g.AttemptCount,
                g.FirstAttemptAt,
                g.LastAttemptAt,
                g.EntityType,
                g.EntityId,
                // The button is offered only for a caller the journal could not attach. One that
                // resolved is already a lead or a client, and offering to create a second would
                // be offering to create a duplicate.
                CanCreateLead: g.EntityId is null))
            .ToList());
    }
}

internal static class ListMissedCallsEndpoint
{
    internal static IEndpointRouteBuilder MapListMissedCalls(this IEndpointRouteBuilder app)
    {
        app.MapGet("calls/missed", Handle)
            .WithName("ListTelephonyMissedCalls")
            .WithSummary("The calls nobody answered, grouped per caller")
            .WithDescription(
                "YOUR missed calls plus the ones attributed to nobody — the agency's 'to "
                + "attribute' queue, which is the call most likely to be forgotten. A colleague's "
                + "is theirs: showing it here would have two agents calling one person back. "
                + "Grouped on a 30-minute gap between consecutive attempts, so somebody who tried "
                + "three times in ten minutes is one entry with attemptCount=3 — and the callId is "
                + "their LAST attempt, which carries the most recent reading of who they are. "
                + "Calls already wrapped up are excluded. Voicemail counts as missed (somebody "
                + "left a message); a FAILED call does not (an operator refusal has nobody waiting "
                + "to be called back). "
                + "No name and no number: read GET calls/{callId}/context for a name, which "
                + "applies your perimeter. Defaults to the last 7 days. "
                + "Requires permission: Telephony.Call.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyCall.Code)
            .Produces<IReadOnlyList<MissedCallDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, CancellationToken ct, DateTimeOffset? since = null)
        => Results.Ok((await sender.Send(new ListMissedCallsQuery(since), ct)).Value);
}
