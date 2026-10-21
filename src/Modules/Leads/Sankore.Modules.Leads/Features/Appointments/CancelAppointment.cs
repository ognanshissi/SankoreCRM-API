namespace Sankore.Modules.Leads.Features.Appointments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

public sealed record CancelAppointmentRequest(string? Reason);

internal sealed record CancelAppointmentCommand(Guid AppointmentId, string? Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Appointment";
    public string? ResourceId => AppointmentId.ToString();
}

/// <summary>The organiser cancels, or a supervisor; the invited colleagues are told.</summary>
internal sealed class CancelAppointmentHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
    : IRequestHandler<CancelAppointmentCommand, Result>
{
    public async Task<Result> Handle(CancelAppointmentCommand cmd, CancellationToken ct)
    {
        var appointment = await db.Appointments.AsTracking()
            .FirstOrDefaultAsync(a => a.Id == cmd.AppointmentId, ct);
        if (appointment is null) return Result.Fail("APPOINTMENT_NOT_FOUND");

        if (appointment.OrganizerId != currentUser.Id && !IsSupervisor())
            return Result.Fail("NOT_APPOINTMENT_ORGANIZER");

        var cancelled = appointment.Cancel(cmd.Reason, clock.GetUtcNow());
        if (cancelled.IsFailure) return cancelled;

        if (appointment.ParticipantIds.Count > 0)
            await publisher.PublishAsync(
                new AppointmentCancelledEvent(appointment.TenantId, appointment.Id, appointment.ParticipantIds.ToList()), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r => r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
