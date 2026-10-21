namespace Sankore.Modules.Leads.Features.Appointments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <param name="DurationMinutes">Null keeps the current duration.</param>
public sealed record RescheduleAppointmentRequest(DateTimeOffset StartAt, int? DurationMinutes);

internal sealed record RescheduleAppointmentCommand(Guid AppointmentId, DateTimeOffset StartAt, int? DurationMinutes)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Appointment";
    public string? ResourceId => AppointmentId.ToString();
}

/// <summary>
/// Moves a rendez-vous — the calendar's « Déplacer à » on a conflict. The organiser moves it, or a
/// supervisor, like a cancellation; the invited colleagues are mailed the new date.
/// </summary>
internal sealed class RescheduleAppointmentHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
    : IRequestHandler<RescheduleAppointmentCommand, Result>
{
    public async Task<Result> Handle(RescheduleAppointmentCommand cmd, CancellationToken ct)
    {
        var appointment = await db.Appointments.AsTracking()
            .FirstOrDefaultAsync(a => a.Id == cmd.AppointmentId, ct);
        if (appointment is null) return Result.Fail("APPOINTMENT_NOT_FOUND");

        if (appointment.OrganizerId != currentUser.Id && !IsSupervisor())
            return Result.Fail("NOT_APPOINTMENT_ORGANIZER");

        var unchanged = appointment.StartAt == cmd.StartAt
            && (cmd.DurationMinutes is null || cmd.DurationMinutes == appointment.DurationMinutes);

        var moved = appointment.Reschedule(cmd.StartAt, cmd.DurationMinutes, clock.GetUtcNow());
        if (moved.IsFailure) return moved;

        // A move to the same slot (a double click) mails nobody.
        if (!unchanged && appointment.ParticipantIds.Count > 0)
            await publisher.PublishAsync(
                new AppointmentRescheduledEvent(
                    appointment.TenantId, appointment.Id, appointment.StartAt, appointment.ParticipantIds.ToList()), ct);

        // With or without invited colleagues: the organiser's own agenda copy follows the move too.
        if (!unchanged)
            await publisher.PublishAsync(new AppointmentChangedIntegrationEvent(appointment.TenantId, appointment.Id), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r => r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
