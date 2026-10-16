namespace Sankore.Modules.Integration.Features.Telephony.Journal;

using System.Globalization;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The journal as a file (TEL-F-08).
///
/// <para>
/// <b>It reuses <see cref="ListTelephonyCallsHandler.BuildAsync"/>, which is the only thing that
/// makes it trustworthy.</b> A second copy of the filters and the perimeter would be a second
/// opinion about who may see what, and the one that drifts is always the one nobody reads — here
/// it would drift into a file somebody mails outside the institution.
/// </para>
///
/// <para>
/// <b>Behind <c>Telephony.Stats.View</c> and not <c>Telephony.Call.View</c>.</b> Reading a screen
/// of calls and carrying a year of them out of the building are different acts: the second is the
/// supervisor-grade permission, which is the one TEL-F-08 names when it says the export is
/// reserved. The PERIMETER still applies on top — a supervisor exports their own branches, not
/// the institution.
/// </para>
///
/// <para>
/// <b>No telephone number and no note, exactly as the screen.</b> An export is where a number
/// would do the most damage: a file is copied, mailed and kept, long after the permission that
/// produced it was revoked. The note is encrypted and a decrypted column here would be the one
/// place an agent's free text about a person leaves the module.
/// </para>
/// </summary>
internal sealed record ExportTelephonyCallsQuery(ListTelephonyCallsQuery Filters)
    : IRequest<Result<TelephonyCallExport>>;

internal sealed record TelephonyCallExport(byte[] Content, string ContentType, string FileName);

/// <summary>
/// The export's row ceiling, as a service rather than a constant.
///
/// <para>
/// <b>It is injected for one reason: the refusal was otherwise untestable.</b> A ceiling of
/// twenty thousand compared against a database count has no seam short of seeding twenty
/// thousand and one rows, and a test that asserted <c>MaxRows == 20_000</c> would say nothing
/// about the comparison's direction or about the promise that NO file is produced. Both of those
/// are what a reviewer relies on.
/// </para>
///
/// <para>
/// A record with a default rather than a configuration section, because nothing suggests an
/// institution should tune this: it is a bound on one response, not a policy. The day it needs to
/// come from configuration, the shape is already right.
/// </para>
/// </summary>
internal sealed record TelephonyExportOptions(
    int MaxRows = ExportTelephonyCallsHandler.DefaultMaxRows);

internal sealed class ExportTelephonyCallsHandler(
    ListTelephonyCallsHandler lister,
    TelephonyExportOptions options,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ExportTelephonyCallsHandler> logger)
    : IRequestHandler<ExportTelephonyCallsQuery, Result<TelephonyCallExport>>
{
    /// <summary>
    /// UTF-8 WITH a byte-order mark, for the reason the mapping export states: Excel otherwise
    /// mangles every accented label, and these rows carry French outcome wording.
    /// </summary>
    internal const string ContentType = "text/csv; charset=utf-8";

    /// <summary>
    /// The default ceiling. Twenty thousand calls is more than a busy branch places in a quarter,
    /// and a period that exceeds it is a period to narrow rather than a file to stream.
    /// </summary>
    internal const int DefaultMaxRows = 20_000;

    internal const string Header =
        "callId;startedAt;direction;status;agentId;agencyId;recordType;recordId;attachment;"
        + "attachmentMethod;outcomeCode;outcomeLabel;countsAsContact;ringSeconds;talkSeconds;"
        + "wrappedUpAt;dismissedAt;recordingStatus;hasNote";

    public async Task<Result<TelephonyCallExport>> Handle(
        ExportTelephonyCallsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var built = await lister.BuildAsync(query.Filters, ct);

        // Relayed rather than swallowed: the whole point of sharing the query is that the file and
        // the screen refuse the same requests. Before BuildAsync validated, this path answered a
        // file of the caller's whole perimeter for a request the screen refused outright.
        if (built.IsFailure)
            return Result.Fail<TelephonyCallExport>(built.Error!);

        var rows = built.Value;

        var total = await rows.CountAsync(ct);

        if (total > options.MaxRows)
        {
            // Refused and NO file produced. A truncated export read as complete is worse than no
            // export: somebody reconciles a quarter against the first twenty thousand rows of it
            // and the figures disagree with the journal for a reason nothing on the page explains.
            logger.LogInformation(
                "Telephony journal export refused for user {UserId} of tenant {TenantId}: "
                + "{RowCount} row(s) exceeds the {Ceiling} ceiling.",
                currentUser.Id, currentUser.TenantId, total, options.MaxRows);

            return Result.Fail<TelephonyCallExport>(TelephonyErrors.ExportTooLarge);
        }

        var items = await rows
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => new
            {
                c.Id, c.StartedAt, c.Direction, c.Status, c.CrmUserId, c.AgencyId,
                c.EntityType, c.EntityId, c.MatchStatus, c.MatchMethod, c.OutcomeCode,
                c.RingSeconds, c.TalkSeconds, c.WrappedUpAt, c.DismissedAt, c.RecordingStatus,
                HasNote = c.NoteEnc != null,
            })
            .ToListAsync(ct);

        var codes = items
            .Select(i => i.OutcomeCode)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var outcomes = codes.Count == 0
            ? []
            : await lister.OutcomeLabelsAsync(codes, ct);

        var csv = new StringBuilder();
        csv.AppendLine(Header);

        foreach (var i in items)
        {
            var outcome = i.OutcomeCode is { } code && outcomes.TryGetValue(code, out var o)
                ? o
                : null;

            // Semicolon-separated, like the mapping export: a comma is the decimal separator in
            // the locales this platform serves, and a French Excel opens a comma-separated file
            // as one column.
            csv.Append(i.Id).Append(';')
                .Append(i.StartedAt.ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append(i.Direction).Append(';')
                .Append(i.Status).Append(';')
                .Append(i.CrmUserId?.ToString()).Append(';')
                .Append(i.AgencyId?.ToString()).Append(';')
                .Append(i.EntityType).Append(';')
                .Append(i.EntityId?.ToString()).Append(';')
                .Append(i.MatchStatus).Append(';')
                .Append(i.MatchMethod?.ToString()).Append(';')
                .Append(i.OutcomeCode).Append(';')
                .Append(Escape(outcome?.Label)).Append(';')
                .Append(outcome?.CountsAsContact.ToString()).Append(';')
                .Append(i.RingSeconds?.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(i.TalkSeconds?.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(i.WrappedUpAt?.ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append(i.DismissedAt?.ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append(i.RecordingStatus).Append(';')
                .Append(i.HasNote ? "true" : "false")
                .AppendLine();
        }

        // Logged at information with the ROW COUNT and no filter values: an export is an act
        // somebody may have to account for, and the count is what tells a reviewer how much left
        // the building. The filters could name a single agent, which is a fact about a person.
        logger.LogInformation(
            "Telephony journal exported by user {UserId} of tenant {TenantId}: {RowCount} row(s).",
            currentUser.Id, currentUser.TenantId, items.Count);

        var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        return Result.Ok(new TelephonyCallExport(
            // The BOM is prepended explicitly rather than left to the encoding, so the bytes are
            // the same whatever the host's default encoding happens to be.
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())],
            ContentType,
            $"appels-{stamp}.csv"));
    }

    /// <summary>
    /// Quotes a field that could carry a separator. Only the outcome label can: every other
    /// column is an identifier, an instant, a number or an enum name.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.Contains(';', StringComparison.Ordinal)
               || value.Contains('"', StringComparison.Ordinal)
               || value.Contains('\n', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}

internal static class ExportTelephonyCallsEndpoint
{
    internal static IEndpointRouteBuilder MapExportTelephonyCalls(this IEndpointRouteBuilder app)
    {
        app.MapGet("calls/export.csv", Handle)
            .WithName("ExportTelephonyCalls")
            .WithSummary("The call journal as a CSV file")
            .WithDescription(
                "Takes the SAME filters as GET telephony/calls and applies the SAME perimeter — "
                + "the query is shared, so the file and the screen cannot disagree about who may "
                + "see what. "
                + "Behind Telephony.Stats.View rather than Telephony.Call.View: reading a screen "
                + "of calls and carrying a year of them out of the building are different acts. "
                + "The perimeter still applies on top, so a supervisor exports their own "
                + "branches. "
                + "Columns carry identifiers, instants, durations and the outcome — NO telephone "
                + "number and NO note, exactly as the screen. hasNote says one exists. "
                + "UTF-8 with a byte-order mark and semicolon-separated, because a French Excel "
                + "opens a comma-separated file as one column and mangles accents without the "
                + "BOM. "
                + "A period matching more than 20 000 calls is refused (422 "
                + "TELEPHONY_EXPORT_TOO_LARGE): narrow the period rather than stream a quarter. "
                + "The export is logged with its row count. "
                + "Requires permission: Telephony.Stats.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyStats.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, ExportTelephonyCallsHandler.ContentType)
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
        TelephonyCallQueue? queue = null)
    {
        var result = await sender.Send(
            new ExportTelephonyCallsQuery(new ListTelephonyCallsQuery(
                from, to, crmUserId, agencyId, direction, status, attachment, outcomeCode,
                entityType, entityId, queue)),
            ct);

        return result.IsSuccess
            ? Results.File(result.Value.Content, result.Value.ContentType, result.Value.FileName)
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}
