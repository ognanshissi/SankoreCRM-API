namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Sankore.Modules.Integration.Domain;

/// <summary>An agenda as the provider lists it.</summary>
internal sealed record ProviderCalendar(
    string Id, string Name, string? Color, bool IsPrimary, bool IsReadOnly, bool IsSubscription);

/// <summary>One event, in UTC. All-day events start and end at midnight UTC of their dates.</summary>
internal sealed record ProviderEvent(
    string Id, string? Title, string? Location, DateTimeOffset StartAt, DateTimeOffset EndAt, bool IsAllDay, CalendarShowAs ShowAs);

/// <param name="Removed">Ids the provider reports deleted or cancelled.</param>
/// <param name="NextPage">More of THIS read: pass it back as the cursor.</param>
/// <param name="DeltaCursor">The read is complete: keep it for the next run.</param>
/// <param name="CursorExpired">The provider no longer knows the cursor: read the agenda whole.</param>
internal sealed record ProviderEventPage(
    IReadOnlyList<ProviderEvent> Changed,
    IReadOnlyList<string> Removed,
    string? NextPage,
    string? DeltaCursor,
    bool CursorExpired = false);

/// <summary>
/// A SANKORÉ rendez-vous as it is written into a participant's agenda (lot 4). In UTC; the
/// provider shows it in the agenda's own zone.
/// </summary>
internal sealed record ProviderEventDraft(string Title, string Body, string? Location, DateTimeOffset StartAt, DateTimeOffset EndAt);

/// <summary>
/// Microsoft Graph agendas. <c>calendarView/delta</c> gives the occurrences of a window — recurring
/// series already expanded — and a delta link that later returns only what changed in it. Times are
/// asked in UTC (<c>Prefer: outlook.timezone="UTC"</c>), so nothing here converts a zone.
/// </summary>
internal sealed partial class MicrosoftConnectedAccountProvider
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string Prefer = "outlook.timezone=\"UTC\", odata.maxpagesize=200";

    public async Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await GetAsync(
            new Uri($"{Graph}/me/calendars?$select=id,name,hexColor,canEdit,isDefaultCalendar,owner&$top=100"), accessToken, ct);

        ThrowOnFailure(status, "calendars");

        return body!.Value.GetProperty("value").EnumerateArray().Select(c => new ProviderCalendar(
            c.GetProperty("id").GetString()!,
            c.TryGetProperty("name", out var n) ? n.GetString() ?? "Agenda" : "Agenda",
            c.TryGetProperty("hexColor", out var h) && h.GetString() is { Length: > 0 } hex ? hex : null,
            c.TryGetProperty("isDefaultCalendar", out var d) && d.ValueKind == JsonValueKind.True,
            !(c.TryGetProperty("canEdit", out var e) && e.ValueKind == JsonValueKind.True),
            // Another person's agenda shared with this account, or a subscribed one.
            !(c.TryGetProperty("canEdit", out var e2) && e2.ValueKind == JsonValueKind.True)
                && !(c.TryGetProperty("isDefaultCalendar", out var d2) && d2.ValueKind == JsonValueKind.True)))
            .ToList();
    }

    public async Task<ProviderEventPage> ReadEventsAsync(
        string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        // The cursor IS the next or delta link Graph handed back — an absolute URL on its own host.
        var uri = cursor is { Length: > 0 } link && link.StartsWith(Graph, StringComparison.Ordinal)
            ? new Uri(link)
            : new Uri(QueryHelpers.AddQueryString(
                $"{Graph}/me/calendars/{Uri.EscapeDataString(calendarId)}/calendarView/delta",
                new Dictionary<string, string?>
                {
                    ["startDateTime"] = windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["endDateTime"] = windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                }));

        var (status, body) = await GetAsync(uri, accessToken, ct, Prefer);

        // A delta link Graph no longer honours: start the window again.
        if (status is HttpStatusCode.Gone || (status == HttpStatusCode.BadRequest && cursor is not null))
            return new ProviderEventPage([], [], null, null, CursorExpired: true);

        ThrowOnFailure(status, "events");

        var changed = new List<ProviderEvent>();
        var removed = new List<string>();

        foreach (var e in body!.Value.GetProperty("value").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;

            if (e.TryGetProperty("@removed", out _) || (e.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True))
            {
                removed.Add(id);
                continue;
            }

            if (!e.TryGetProperty("start", out var start) || !e.TryGetProperty("end", out var end)) continue;

            changed.Add(new ProviderEvent(
                id,
                e.TryGetProperty("subject", out var s) ? s.GetString() : null,
                e.TryGetProperty("location", out var l) && l.TryGetProperty("displayName", out var ld) ? ld.GetString() : null,
                GraphTime(start),
                GraphTime(end),
                e.TryGetProperty("isAllDay", out var a) && a.ValueKind == JsonValueKind.True,
                (e.TryGetProperty("showAs", out var sa) ? sa.GetString() : null) switch
                {
                    "free" => CalendarShowAs.Free,
                    "tentative" => CalendarShowAs.Tentative,
                    "oof" => CalendarShowAs.OutOfOffice,
                    "workingElsewhere" => CalendarShowAs.WorkingElsewhere,
                    _ => CalendarShowAs.Busy,
                }));
        }

        return new ProviderEventPage(
            changed,
            removed,
            body.Value.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null,
            body.Value.TryGetProperty("@odata.deltaLink", out var delta) ? delta.GetString() : null);
    }

    public async Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post,
            new Uri($"{Graph}/me/calendars/{Uri.EscapeDataString(calendarId)}/events"), accessToken, GraphEvent(draft), ct);
        ThrowOnFailure(status, "event_create");
        return body!.Value.GetProperty("id").GetString()!;
    }

    public async Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct)
    {
        var (status, _) = await SendAsync(HttpMethod.Patch,
            new Uri($"{Graph}/me/events/{Uri.EscapeDataString(eventId)}"), accessToken, GraphEvent(draft), ct);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;
        ThrowOnFailure(status, "event_update");
        return true;
    }

    public async Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct)
    {
        var (status, _) = await SendAsync(HttpMethod.Delete,
            new Uri($"{Graph}/me/events/{Uri.EscapeDataString(eventId)}"), accessToken, null, ct);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;
        ThrowOnFailure(status, "event_delete");
    }

    private static object GraphEvent(ProviderEventDraft d) => new Dictionary<string, object?>
    {
        ["subject"] = d.Title,
        ["body"] = new { contentType = "text", content = d.Body },
        ["start"] = new { dateTime = d.StartAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
        ["end"] = new { dateTime = d.EndAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
        ["location"] = new { displayName = d.Location ?? string.Empty },
        ["showAs"] = "busy",
    };

    /// <summary><c>{"dateTime":"2026-10-21T09:00:00.0000000","timeZone":"UTC"}</c> — UTC because we asked for it.</summary>
    private static DateTimeOffset GraphTime(JsonElement value) =>
        DateTime.TryParse(value.GetProperty("dateTime").GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : DateTimeOffset.MinValue;

    private static void ThrowOnFailure(HttpStatusCode status, string what)
    {
        if ((int)status is >= 200 and < 300) return;
        throw new ProviderAuthException($"{what}_http_{(int)status}", revoked: status == HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Google agendas. <c>singleEvents=true</c> expands recurring series into occurrences inside the
/// window; the last page carries a <c>nextSyncToken</c> that returns only what changed. An
/// incremental read is NOT limited to the window — Google refuses <c>timeMin</c> with a sync token —
/// so the sync filters what falls outside it.
/// </summary>
internal sealed partial class GoogleConnectedAccountProvider
{
    private const string Api = "https://www.googleapis.com/calendar/v3";

    public async Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        var (status, body) = await GetAsync(new Uri($"{Api}/users/me/calendarList?maxResults=250"), accessToken, ct);
        ThrowOnFailure(status, "calendars");

        return body!.Value.GetProperty("items").EnumerateArray().Select(c =>
        {
            var id = c.GetProperty("id").GetString()!;
            var role = c.TryGetProperty("accessRole", out var r) ? r.GetString() : null;

            return new ProviderCalendar(
                id,
                c.TryGetProperty("summaryOverride", out var so) && so.GetString() is { Length: > 0 } over
                    ? over
                    : c.TryGetProperty("summary", out var s) ? s.GetString() ?? "Agenda" : "Agenda",
                c.TryGetProperty("backgroundColor", out var b) ? b.GetString() : null,
                c.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
                role is not ("owner" or "writer"),
                // Public holidays, birthdays, someone else's agenda: everything the account does not own.
                id.EndsWith("@group.v.calendar.google.com", StringComparison.Ordinal) || role is "reader" or "freeBusyReader");
        }).ToList();
    }

    public async Task<ProviderEventPage> ReadEventsAsync(
        string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["singleEvents"] = "true",
            ["maxResults"] = "250",
            ["showDeleted"] = "true",
        };

        // « page:{token} » continues THIS read, « sync:{token} » starts an incremental one.
        string? syncToken = null;

        if (cursor?.StartsWith("page:", StringComparison.Ordinal) == true)
        {
            var parts = cursor["page:".Length..].Split('|', 2);
            query["pageToken"] = parts[0];
            if (parts.Length == 2 && parts[1].Length > 0) syncToken = parts[1];
        }
        else if (cursor?.StartsWith("sync:", StringComparison.Ordinal) == true)
        {
            syncToken = cursor["sync:".Length..];
        }

        if (syncToken is not null)
        {
            query["syncToken"] = syncToken;
        }
        else
        {
            query["timeMin"] = windowStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            query["timeMax"] = windowEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        var (status, body) = await GetAsync(
            new Uri(QueryHelpers.AddQueryString($"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events", query)), accessToken, ct);

        if (status == HttpStatusCode.Gone) return new ProviderEventPage([], [], null, null, CursorExpired: true);
        ThrowOnFailure(status, "events");

        var changed = new List<ProviderEvent>();
        var removed = new List<string>();

        var items = body!.Value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : [];

        foreach (var e in items)
        {
            var id = e.GetProperty("id").GetString()!;

            if (e.TryGetProperty("status", out var st) && st.GetString() == "cancelled")
            {
                removed.Add(id);
                continue;
            }

            if (!e.TryGetProperty("start", out var start) || !e.TryGetProperty("end", out var end)) continue;

            var allDay = start.TryGetProperty("date", out _);
            var eventType = e.TryGetProperty("eventType", out var et) ? et.GetString() : null;

            changed.Add(new ProviderEvent(
                id,
                e.TryGetProperty("summary", out var s) ? s.GetString() : null,
                e.TryGetProperty("location", out var l) ? l.GetString() : null,
                GoogleTime(start),
                GoogleTime(end),
                allDay,
                eventType switch
                {
                    "outOfOffice" => CalendarShowAs.OutOfOffice,
                    "workingLocation" => CalendarShowAs.WorkingElsewhere,
                    _ when e.TryGetProperty("transparency", out var t) && t.GetString() == "transparent" => CalendarShowAs.Free,
                    _ => CalendarShowAs.Busy,
                }));
        }

        var nextPage = body.Value.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        var nextSync = body.Value.TryGetProperty("nextSyncToken", out var ns) ? ns.GetString() : null;

        return new ProviderEventPage(
            changed,
            removed,
            // An incremental read paged in several requests must keep its sync token on every page.
            nextPage is null ? null : $"page:{nextPage}|{syncToken}",
            nextSync is null ? null : $"sync:{nextSync}");
    }

    public async Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post,
            new Uri($"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events"), accessToken, GoogleEvent(draft), ct);
        ThrowOnFailure(status, "event_create");
        return body!.Value.GetProperty("id").GetString()!;
    }

    public async Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Patch,
            new Uri($"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}"), accessToken, GoogleEvent(draft), ct);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;
        ThrowOnFailure(status, "event_update");

        // A copy deleted by hand stays in Google's bin as « cancelled » and a PATCH answers 200 on
        // it: treat it as gone, or the copy would live on where nobody sees it.
        return !(body?.TryGetProperty("status", out var st) == true && st.GetString() == "cancelled");
    }

    public async Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct)
    {
        var (status, _) = await SendAsync(HttpMethod.Delete,
            new Uri($"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}"), accessToken, null, ct);
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return;
        ThrowOnFailure(status, "event_delete");
    }

    private static object GoogleEvent(ProviderEventDraft d) => new Dictionary<string, object?>
    {
        ["summary"] = d.Title,
        ["description"] = d.Body,
        ["location"] = d.Location ?? string.Empty,
        ["start"] = new { dateTime = d.StartAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
        ["end"] = new { dateTime = d.EndAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) },
        ["transparency"] = "opaque",
    };

    /// <summary><c>{"dateTime":"…+00:00"}</c>, or <c>{"date":"2026-10-21"}</c> for an all-day event.</summary>
    private static DateTimeOffset GoogleTime(JsonElement value)
    {
        if (value.TryGetProperty("dateTime", out var dt)
            && DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return at.ToUniversalTime();

        if (value.TryGetProperty("date", out var d)
            && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return DateTimeOffset.MinValue;
    }

    private static void ThrowOnFailure(HttpStatusCode status, string what)
    {
        if ((int)status is >= 200 and < 300) return;
        throw new ProviderAuthException($"{what}_http_{(int)status}", revoked: status == HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// Simulated agendas (Development): the agendas of the mock-ups, with events laid out on the
/// current week, and a delta cursor that answers « nothing changed » afterwards.
/// </summary>
internal sealed partial class FakeConnectedAccountProvider
{
    public Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderCalendar>>(Provider == ConnectedAccountProvider.Microsoft
            ?
            [
                new("fake-cal-main", "Calendrier", "#2563EB", true, false, false),
                new("fake-cal-agency", "Réunions d’agence", "#60A5FA", false, false, false),
            ]
            :
            [
                new("fake-cal-personal", "Personnel", "#DC2626", true, false, false),
                new("fake-cal-family", "Famille", "#EA580C", false, false, false),
                new("fake-cal-holidays", "Jours fériés en Côte d’Ivoire", "#16A34A", false, true, true),
            ]);

    public Task<ProviderEventPage> ReadEventsAsync(
        string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        if (cursor is { Length: > 0 }) return Task.FromResult(new ProviderEventPage([], [], null, cursor));

        // Monday of the current week, UTC.
        var today = DateTime.UtcNow.Date;
        var monday = new DateTimeOffset(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), TimeSpan.Zero);

        ProviderEvent At(string id, string title, int day, int hour, int minutes, double durationHours, string? location = null, CalendarShowAs showAs = CalendarShowAs.Busy) =>
            new($"{calendarId}-{id}", title, location, monday.AddDays(day).AddHours(hour).AddMinutes(minutes),
                monday.AddDays(day).AddHours(hour).AddMinutes(minutes).AddHours(durationHours), false, showAs);

        IReadOnlyList<ProviderEvent> events = calendarId switch
        {
            "fake-cal-main" => [At("1", "Comité de crédit", 3, 10, 0, 2, "Siège"), At("2", "Revue du portefeuille", 4, 8, 30, 1)],
            "fake-cal-agency" => [At("1", "Point équipe Agence", 0, 11, 0, 1, "Agence 2"), At("2", "Formation conformité", 1, 14, 0, 1.5)],
            "fake-cal-personal" => [At("1", "Sport", 1, 18, 0, 1), At("2", "Messe", 6, 10, 0, 2)],
            "fake-cal-family" => [At("1", "École des enfants", 4, 17, 0, 1)],
            "fake-cal-holidays" =>
            [
                new($"{calendarId}-1", "Jour férié (simulé)", null, monday.AddDays(5), monday.AddDays(6), true, CalendarShowAs.Free),
            ],
            _ => [],
        };

        return Task.FromResult(new ProviderEventPage(events, [], null, $"fake-delta:{calendarId}"));
    }

    public Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct) =>
        Task.FromResult($"fake-copy-{Guid.NewGuid():N}");

    public Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct) =>
        Task.FromResult(true);

    public Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct) => Task.CompletedTask;
}
