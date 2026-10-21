namespace Sankore.Modules.Leads.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// A rendez-vous was planned, moved or cancelled. Published through M13's outbox with the change
/// itself, and carrying only the id: whoever cares — the copy into the participants' connected
/// agendas — reads the appointment as it stands when the message arrives, so two changes in a row
/// never apply out of order.
/// </summary>
public sealed record AppointmentChangedIntegrationEvent(Guid TenantId, Guid AppointmentId) : IntegrationEventBase;

/// <summary>
/// A rendez-vous as another module may show it in a calendar: when, where, who. Deliberately
/// WITHOUT its description and without the lead or client it concerns — a copy in a personal
/// Outlook or Google agenda leaves the institution, and the title is all it needs to be recognised.
/// </summary>
/// <param name="ParticipantIds">The invited colleagues, organiser excluded.</param>
public sealed record AppointmentCalendarEntry(
    Guid Id,
    string Title,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool IsCancelled,
    string? Location,
    string? MeetingUrl,
    Guid OrganizerId,
    IReadOnlyList<Guid> ParticipantIds);
