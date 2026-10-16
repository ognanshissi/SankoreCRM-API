namespace Sankore.Modules.Integration.Features.Telephony.Journal;

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
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Which calls a filtered page of the journal should contain (TEL-B-08, TEL-F-07, TEL-F-08).
/// </summary>
/// <param name="Queue">
/// One of the three work queues, instead of hand-assembling its filters — see
/// <see cref="TelephonyCallQueue"/>. A queue and the individual filters compose: "to qualify, for
/// this agent, last week" is a reasonable question.
/// </param>
/// <param name="EntityType">
/// With <paramref name="EntityId"/>, the calls of ONE record — which is what a lead's or a
/// client's timeline asks for (TEL-F-07). The pair is checked together: an id with no type, or a
/// type this module does not know, is refused rather than silently ignored.
/// </param>
internal sealed record ListTelephonyCallsQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? CrmUserId = null,
    Guid? AgencyId = null,
    TelCallDirection? Direction = null,
    TelCallStatus? Status = null,
    TelMatchStatus? Attachment = null,
    string? OutcomeCode = null,
    string? EntityType = null,
    Guid? EntityId = null,
    TelephonyCallQueue? Queue = null,
    int Page = 1,
    int PageSize = 20)
    : IRequest<Result<PagedResult<TelephonyCallListItemDto>>>;

/// <summary>
/// The three piles of work TEL-F-08 puts on tabs. Named here rather than left to the front to
/// assemble from filters, because each one is a RULE and a screen that rebuilt it would drift:
/// the day "to qualify" learns about dismissal, one of the two definitions would not.
/// </summary>
internal enum TelephonyCallQueue
{
    /// <summary>
    /// Not attached to any record — several candidates, or a number nobody recognises. The pile
    /// that makes the journal untrustworthy if nobody works it.
    /// </summary>
    ToAttach,

    /// <summary>
    /// Ended and never qualified. Excludes the ones an agent closed on purpose with a reason:
    /// a dismissal is an answer, and an answered item is not outstanding work.
    /// </summary>
    ToQualify,

    /// <summary>
    /// Attributed to nobody — a call from a provider account no link resolves. The agency's queue,
    /// and the one an administrator owns, because the repair is a user link and not a call.
    /// </summary>
    ToAssign,
}

/// <param name="CanListen">
/// Whether this caller may hear the recording. Computed here rather than probed by the front with
/// a request that would 404: the journal is already perimeter-filtered, so the question reduces to
/// "is there audio and does this caller hold the permission".
/// </param>
/// <param name="HasNote">
/// That a note exists, never its content. The note is encrypted, is excluded from every event and
/// from every timeline summary, and a list is the last place it should surface — but a reader
/// needs to know there is something to open.
/// </param>
internal sealed record TelephonyCallListItemDto(
    Guid CallId,
    Guid ConnectionId,
    string Direction,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt,
    int? RingSeconds,
    int? TalkSeconds,
    Guid? CrmUserId,
    Guid? AgencyId,
    string? EntityType,
    Guid? EntityId,
    string MatchStatus,
    string? MatchMethod,
    string? OutcomeCode,
    string? OutcomeLabel,
    bool? CountsAsContact,
    DateTimeOffset? WrappedUpAt,
    DateTimeOffset? DismissedAt,
    string RecordingStatus,
    bool CanListen,
    bool HasNote);

/// <summary>
/// The journal, filtered and paged — the read TEL-B-08 describes and TEL-B-20 never exposed.
///
/// <para>
/// <b>It is PERIMETER-FILTERED, and that is the whole security story of this endpoint.</b> Until
/// now the only call lists were "my missed calls" and "the calls of this record", each scoped by
/// construction. A list over every call of a tenant is a new kind of surface: without a perimeter
/// it would hand any holder of <c>Telephony.Call.View</c> the complete telephone activity of
/// every branch — who spoke to whom, when, for how long, and with what outcome. That is the same
/// defect the manual attachment had before TEL-B-19, and the same fix: the agency a call belonged
/// to AT THE TIME, compared against the caller's own scope.
/// </para>
///
/// <para>
/// <b>Three things are visible: my own calls, the ones nobody is attributed, and the agencies I
/// cover.</b> The second is deliberate and is not a hole — it is the "to attribute" queue the
/// missed-call list already shows everyone, for the reason it states: a call from an unlinked
/// account is the call most likely to be forgotten, and hiding it until somebody repairs a
/// settings row loses exactly the history the repair explains. An unrestricted caller — a
/// super-user, which <c>IAgencyScopeProvider</c> signals with a null set — sees the tenant whole.
/// </para>
///
/// <para>
/// <b>No telephone number, in any field, ever.</b> Consistent with every other telephony read:
/// the screen-pop context returns none, the missed list returns none. A journal is the surface
/// where a number would be most convenient and least defensible — it would put the complete call
/// history of an institution's customers behind one permission, in a form an export could carry
/// out of the building.
/// </para>
/// </summary>
internal sealed class ListTelephonyCallsHandler(
    IntegrationDbContext db,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
    : IRequestHandler<ListTelephonyCallsQuery, Result<PagedResult<TelephonyCallListItemDto>>>
{
    /// <summary>
    /// A ceiling on one page. A journal is the one telephony read somebody will try to drain in a
    /// loop, and a hundred rows is already more than a screen shows.
    /// </summary>
    internal const int MaxPageSize = 100;

    public async Task<Result<PagedResult<TelephonyCallListItemDto>>> Handle(
        ListTelephonyCallsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var built = await BuildAsync(query, ct);

        if (built.IsFailure)
            return Result.Fail<PagedResult<TelephonyCallListItemDto>>(built.Error!);

        var rows = built.Value;

        var total = await rows.CountAsync(ct);

        // Newest first: a journal is read from the top, and the call somebody is looking for is
        // almost always the recent one. StartedAt and then Id, so two calls that began in the
        // same millisecond order deterministically and a page boundary cannot show one row twice.
        var items = await rows
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id, c.ConnectionId, c.Direction, c.Status, c.StartedAt, c.AnsweredAt, c.EndedAt,
                c.RingSeconds, c.TalkSeconds, c.CrmUserId, c.AgencyId, c.EntityType, c.EntityId,
                c.MatchStatus, c.MatchMethod, c.OutcomeCode, c.WrappedUpAt, c.DismissedAt,
                c.RecordingStatus,

                // The note's PRESENCE, computed in the database. Selecting the column and testing
                // it here would pull every encrypted note of the page into this process for a
                // boolean.
                HasNote = c.NoteEnc != null,
            })
            .ToListAsync(ct);

        // The outcome labels for the codes on THIS page, in one query. A join would be cheaper in
        // SQL and wrong in meaning: the label is the tenant's current wording, and a call whose
        // outcome was later renamed should read with the new name — which is also what the
        // indicators do.
        var codes = items
            .Select(i => i.OutcomeCode)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var outcomes = codes.Count == 0
            ? []
            : await OutcomeLabelsAsync(codes, ct);

        // Read off the token's own claims, which is how AuthorizationPolicies renders every
        // permission — so this cannot disagree with what the player endpoint would check.
        var mayListen = httpContextAccessor.HttpContext?.User.HasClaim(
            "permission", Permissions.CanListenTelephonyRecording.Code) == true;

        return Result.Ok(new PagedResult<TelephonyCallListItemDto>(
            [.. items.Select(i =>
            {
                var outcome = i.OutcomeCode is { } code && outcomes.TryGetValue(code, out var o)
                    ? o
                    : null;

                return new TelephonyCallListItemDto(
                    CallId: i.Id,
                    ConnectionId: i.ConnectionId,
                    Direction: i.Direction.ToString(),
                    Status: i.Status.ToString(),
                    StartedAt: i.StartedAt,
                    AnsweredAt: i.AnsweredAt,
                    EndedAt: i.EndedAt,
                    RingSeconds: i.RingSeconds,
                    TalkSeconds: i.TalkSeconds,
                    CrmUserId: i.CrmUserId,
                    AgencyId: i.AgencyId,
                    EntityType: i.EntityType,
                    EntityId: i.EntityId,
                    MatchStatus: i.MatchStatus.ToString(),
                    MatchMethod: i.MatchMethod?.ToString(),
                    OutcomeCode: i.OutcomeCode,
                    OutcomeLabel: outcome?.Label,
                    CountsAsContact: outcome?.CountsAsContact,
                    WrappedUpAt: i.WrappedUpAt,
                    DismissedAt: i.DismissedAt,
                    RecordingStatus: i.RecordingStatus.ToString(),
                    CanListen: mayListen && i.RecordingStatus == TelRecordingStatus.Available,
                    HasNote: i.HasNote);
            })],
            total,
            page,
            pageSize));
    }

    /// <summary>
    /// The current wording of a set of outcome codes. Shared with the CSV export for the same
    /// reason the query is: one definition of what a code is called.
    ///
    /// <para>
    /// A lookup and not a join, deliberately. The label is the tenant's CURRENT wording, so a
    /// call whose outcome was renamed last month reads with the new name — the same choice the
    /// indicators make, and the one an institution correcting a label expects.
    /// </para>
    /// </summary>
    internal async Task<Dictionary<string, TelephonyOutcomeLabel>> OutcomeLabelsAsync(
        IReadOnlyCollection<string?> codes, CancellationToken ct)
    {
        var wanted = codes.Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).ToList();

        if (wanted.Count == 0) return new Dictionary<string, TelephonyOutcomeLabel>(StringComparer.Ordinal);

        return await db.TelCallOutcomes
            .Where(o => wanted.Contains(o.Code))
            .Select(o => new TelephonyOutcomeLabel(o.Code, o.Label, o.CountsAsContact))
            .ToDictionaryAsync(o => o.Code, o => o, StringComparer.Ordinal, ct);
    }

    /// <summary>
    /// The filtered query, perimeter included. Shared with the CSV export so the file and the
    /// screen can never disagree about what the filters meant.
    ///
    /// <para>
    /// <b>It returns a <c>Result</c>, and that shape is the fix for a real defect.</b> The entity
    /// validation used to live in <c>Handle</c>, so the export — which calls this method directly
    /// — never reached it: <c>?entityType=Invoice</c> was refused on the screen and answered a
    /// file of the whole perimeter. Sharing the query was supposed to make the two agree, and it
    /// did not, because only one of them validated. Validation that lives HERE cannot be skipped
    /// by a caller, which is the property the sharing was for.
    /// </para>
    /// </summary>
    internal async Task<Result<IQueryable<TelCall>>> BuildAsync(
        ListTelephonyCallsQuery query, CancellationToken ct)
    {
        // An entity type this module does not know.
        if (query.EntityType is { } entityType && !TelEntityTypes.IsKnown(entityType))
            return Result.Fail<IQueryable<TelCall>>(TelephonyErrors.EntityTypeUnknown);

        // Half a reference, which USED TO BE SILENTLY IGNORED — and the failure mode was a
        // widening, not a narrowing: a lead's history that lost one query parameter rendered the
        // caller's whole perimeter as if it were that lead's calls. Refused, which is what the
        // aggregate's own Attach already does with the same pair.
        if (query.EntityType is null != query.EntityId is null)
            return Result.Fail<IQueryable<TelCall>>(TelephonyErrors.EntityReferenceIncomplete);

        var me = currentUser.Id;

        // No tenant predicate: the global query filter scopes it.
        var rows = db.TelCalls.AsQueryable();

        var scope = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, me, ct);

        // Null is UNRESTRICTED — a super-user or a tenant-wide account — and the contract is
        // explicit that it must be read as "apply no agency filter", never as "no access".
        if (scope is not null)
        {
            var visible = scope.ToList();

            rows = rows.Where(c =>
                c.CrmUserId == me
                || c.CrmUserId == null
                || (c.AgencyId != null && visible.Contains(c.AgencyId.Value)));
        }

        if (query.From is { } from) rows = rows.Where(c => c.StartedAt >= from);
        if (query.To is { } to) rows = rows.Where(c => c.StartedAt <= to);
        if (query.CrmUserId is { } user) rows = rows.Where(c => c.CrmUserId == user);
        if (query.AgencyId is { } agency) rows = rows.Where(c => c.AgencyId == agency);
        if (query.Direction is { } direction) rows = rows.Where(c => c.Direction == direction);
        if (query.Status is { } status) rows = rows.Where(c => c.Status == status);
        if (query.Attachment is { } match) rows = rows.Where(c => c.MatchStatus == match);

        if (query.OutcomeCode is { } code && !string.IsNullOrWhiteSpace(code))
        {
            var normalised = code.Trim().ToUpperInvariant();

            rows = rows.Where(c => c.OutcomeCode == normalised);
        }

        if (query.EntityType is { } type && query.EntityId is { } id)
            rows = rows.Where(c => c.EntityType == type && c.EntityId == id);

        return Result.Ok(query.Queue switch
        {
            // Anything but a settled attachment. Ambiguous AND Unknown together, because the work
            // is the same — somebody has to say which record this was about.
            TelephonyCallQueue.ToAttach =>
                rows.Where(c => c.MatchStatus != TelMatchStatus.Matched),

            // Ended, unqualified, and not closed on purpose. The three conditions are one rule.
            //
            // It deliberately INCLUDES Failed calls, where the missed-call list deliberately
            // excludes them — the two piles answer different questions. That list asks "who is
            // waiting for a call back", and nobody waits after an operator refusal. This one asks
            // "which calls have no outcome recorded", and an invalid number is exactly the sort
            // of thing an institution wants written down: WRONG_NUMBER is one of the seeded
            // outcomes.
            TelephonyCallQueue.ToQualify =>
                rows.Where(c => c.EndedAt != null
                             && c.WrappedUpAt == null
                             && c.DismissedAt == null),

            TelephonyCallQueue.ToAssign => rows.Where(c => c.CrmUserId == null),

            _ => rows,
        });
    }
}

internal static class ListTelephonyCallsEndpoint
{
    internal static IEndpointRouteBuilder MapListTelephonyCalls(this IEndpointRouteBuilder app)
    {
        app.MapGet("calls", Handle)
            .WithName("ListTelephonyCalls")
            .WithSummary("The call journal, filtered and paged")
            .WithDescription(
                "PERIMETER-FILTERED, and that is the point: you see your own calls, the ones "
                + "nobody is attributed — the agency's 'to attribute' queue, which is the call "
                + "most likely to be forgotten — and the calls of the agencies you cover. A "
                + "tenant-wide account sees everything. The agency is the one the call belonged "
                + "to AT THE TIME, so an agent moving branch does not take their history with "
                + "them. "
                + "queue=ToAttach|ToQualify|ToAssign are the three work piles, each a rule rather "
                + "than a filter combination: ToAttach is anything not settled, ToQualify is "
                + "ended-and-unqualified-and-not-dismissed, ToAssign is attributed to nobody. "
                + "They compose with the other filters. "
                + "entityType + entityId together give the calls of ONE record, which is what a "
                + "lead's or a client's history asks for. "
                + "canListen says whether YOU may hear the recording, so a screen does not have "
                + "to probe for a 404. hasNote says a note exists and never what it says — notes "
                + "are encrypted and never leave the module. "
                + "NO TELEPHONE NUMBER is returned in any field. Newest first. 100 rows is the "
                + "page ceiling. "
                + "Requires permission: Telephony.Call.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyCall.Code)
            .Produces<PagedResult<TelephonyCallListItemDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? crmUserId = null,
        Guid? agencyId = null,
        TelCallDirection? direction = null,
        TelCallStatus? status = null,
        TelMatchStatus? attachment = null,
        string? outcomeCode = null,
        string? entityType = null,
        Guid? entityId = null,
        TelephonyCallQueue? queue = null,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(
            new ListTelephonyCallsQuery(
                from, to, crmUserId, agencyId, direction, status, attachment, outcomeCode,
                entityType, entityId, queue, page, pageSize),
            ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}


/// <summary>One outcome's current wording and its contact flag.</summary>
internal sealed record TelephonyOutcomeLabel(string Code, string Label, bool CountsAsContact);
