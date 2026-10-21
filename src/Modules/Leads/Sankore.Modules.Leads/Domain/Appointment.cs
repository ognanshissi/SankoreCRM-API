namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

public enum AppointmentMode
{
    InPerson,
    Online,
}

/// <summary>Deduced from the meeting link, never typed: the link is what people click.</summary>
public enum MeetingProvider
{
    Zoom,
    Teams,
    GoogleMeet,
    Other,
}

public enum AppointmentStatus
{
    Scheduled,
    Cancelled,
}

/// <summary>
/// A rendez-vous planned from « Ma journée »: with a lead, with a client, or with nobody in
/// particular (an internal meeting). It may be held online, in which case it carries the Zoom or
/// Teams link, and it may invite colleagues, who are mailed and find it in their own agenda.
///
/// <para>
/// Not a <see cref="LeadActivity"/>. An activity records something that happened on a lead — it is
/// always on a lead and always has a performer. An appointment is a plan, may concern no lead at
/// all, and has several people. A planned activity (its « Date/heure prévue ») still shows in the
/// agenda; the two simply coexist.
/// </para>
///
/// <para>
/// <see cref="ClientId"/> is an opaque M01 reference, like <c>Lead.CustomerId</c>: no foreign key,
/// resolved through <c>ICustomersModule</c>. <see cref="TimeZone"/> is the organiser's (IANA, from
/// the browser): the server is in UTC and nothing else in the platform knows a user's zone, so it
/// is what lets an invitation say « 14:00 » rather than « 14:00 UTC ».
/// </para>
/// </summary>
public sealed class Appointment : ITenant
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int LocationMaxLength = 300;
    public const int MeetingUrlMaxLength = 1000;
    public const int MaxParticipants = 50;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Title { get; private set; } = default!;
    public string? Description { get; private set; }
    public DateTimeOffset StartAt { get; private set; }
    public int DurationMinutes { get; private set; }
    public string TimeZone { get; private set; } = "UTC";
    public AppointmentMode Mode { get; private set; }
    public string? Location { get; private set; }
    public string? MeetingUrl { get; private set; }
    public MeetingProvider? MeetingProvider { get; private set; }
    public Guid? LeadId { get; private set; }
    public Guid? ClientId { get; private set; }
    public Guid OrganizerId { get; private set; }

    /// <summary>The colleagues invited, organiser excluded. Stored as a <c>uuid[]</c>.</summary>
    public List<Guid> ParticipantIds { get; private set; } = [];

    public AppointmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    public DateTimeOffset EndAt => StartAt.AddMinutes(DurationMinutes);

    private Appointment() { }

    public static Result<Appointment> Schedule(
        Guid tenantId,
        Guid organizerId,
        string title,
        DateTimeOffset startAt,
        int durationMinutes,
        string? timeZone,
        AppointmentMode mode,
        string? location,
        string? meetingUrl,
        Guid? leadId,
        Guid? clientId,
        IEnumerable<Guid> participantIds,
        string? description,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title)) return Result.Fail<Appointment>("APPOINTMENT_TITLE_REQUIRED");
        if (startAt <= now) return Result.Fail<Appointment>("APPOINTMENT_IN_PAST");
        if (durationMinutes is < 5 or > 24 * 60) return Result.Fail<Appointment>("APPOINTMENT_DURATION_INVALID");
        if (leadId.HasValue && clientId.HasValue) return Result.Fail<Appointment>("APPOINTMENT_ONE_SUBJECT_ONLY");

        Uri? url = null;
        if (mode == AppointmentMode.Online)
        {
            if (!Uri.TryCreate(meetingUrl?.Trim(), UriKind.Absolute, out url)
                || url.Scheme is not ("https" or "http"))
                return Result.Fail<Appointment>("APPOINTMENT_MEETING_URL_INVALID");
        }

        var participants = participantIds.Where(id => id != Guid.Empty && id != organizerId).Distinct().ToList();
        if (participants.Count > MaxParticipants) return Result.Fail<Appointment>("APPOINTMENT_TOO_MANY_PARTICIPANTS");

        return Result.Ok(new Appointment
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            Title           = title.Trim(),
            Description     = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            StartAt         = startAt,
            DurationMinutes = durationMinutes,
            TimeZone        = ResolveTimeZone(timeZone).Id,
            Mode            = mode,
            // An online meeting has no room; an in-person one no link. Keeping the other half would
            // send people to a link nobody will open, or to a room nobody booked.
            Location        = mode == AppointmentMode.InPerson && !string.IsNullOrWhiteSpace(location) ? location.Trim() : null,
            MeetingUrl      = url?.ToString(),
            MeetingProvider = url is null ? null : ProviderOf(url),
            LeadId          = leadId,
            ClientId        = clientId,
            OrganizerId     = organizerId,
            ParticipantIds  = participants,
            Status          = AppointmentStatus.Scheduled,
            CreatedAt       = now,
        });
    }

    public Result Cancel(string? reason, DateTimeOffset now)
    {
        if (Status == AppointmentStatus.Cancelled) return Result.Fail("APPOINTMENT_ALREADY_CANCELLED");
        if (EndAt <= now) return Result.Fail("APPOINTMENT_ALREADY_HELD");

        Status = AppointmentStatus.Cancelled;
        CancelledAt = now;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return Result.Ok();
    }

    public static MeetingProvider ProviderOf(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        if (host == "zoom.us" || host.EndsWith(".zoom.us")) return Domain.MeetingProvider.Zoom;
        if (host is "teams.microsoft.com" or "teams.live.com" || host.EndsWith(".teams.microsoft.com"))
            return Domain.MeetingProvider.Teams;
        if (host == "meet.google.com") return Domain.MeetingProvider.GoogleMeet;
        return Domain.MeetingProvider.Other;
    }

    /// <summary>An unknown or absent zone falls back to UTC rather than refusing the appointment.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
        => !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)
            ? tz
            : TimeZoneInfo.Utc;
}
