namespace Sankore.Modules.Leads.Features.Appointments;

using Sankore.Shared.Kernel;

/// <summary>
/// Written to the outbox with the appointment, so the invitations go out only for an appointment
/// that was actually saved — and do go out, even when the mail queue is down at that instant.
/// </summary>
public sealed record AppointmentScheduledEvent(
    Guid TenantId,
    Guid AppointmentId,
    IReadOnlyList<Guid> ParticipantIds) : IntegrationEventBase;

/// <summary>Same reason as <see cref="AppointmentScheduledEvent"/>, for the cancellation notice.</summary>
public sealed record AppointmentCancelledEvent(
    Guid TenantId,
    Guid AppointmentId,
    IReadOnlyList<Guid> ParticipantIds) : IntegrationEventBase;
