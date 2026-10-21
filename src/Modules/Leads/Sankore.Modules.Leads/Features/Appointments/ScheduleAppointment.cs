namespace Sankore.Modules.Leads.Features.Appointments;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

public sealed record ScheduleAppointmentRequest(
    string Title,
    DateTimeOffset StartAt,
    int DurationMinutes,
    string? TimeZone,
    AppointmentMode Mode,
    string? Location,
    string? MeetingUrl,
    Guid? LeadId,
    Guid? ClientId,
    IReadOnlyList<Guid>? ParticipantIds,
    string? Description);

internal sealed record ScheduleAppointmentCommand(ScheduleAppointmentRequest Request)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Appointment";
    public string? ResourceId => null;
}

/// <summary>
/// The organiser is the caller, never a field of the request: an audit actor is not
/// caller-supplied. The participants are colleagues of the same tenant; the lead must be one the
/// tenant holds, the client one the caller's agency perimeter reaches.
/// </summary>
internal sealed class ScheduleAppointmentHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    ICustomersModule customers,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
    : IRequestHandler<ScheduleAppointmentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ScheduleAppointmentCommand cmd, CancellationToken ct)
    {
        var req = cmd.Request;
        var tenantId = currentUser.TenantId;

        if (req.LeadId is { } leadId && !await db.Leads.AnyAsync(l => l.Id == leadId, ct))
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        Guid? clientId = null;
        var participantIds = (req.ParticipantIds ?? []).Distinct().ToList();

        // Reads through other modules' DbContexts: the ambient transaction is suppressed, or the
        // second connection promotes it to a distributed transaction Npgsql refuses.
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            if (req.ClientId is { } requested)
            {
                // A merged client is followed to its survivor; one outside the caller's agencies
                // reads as absent, as everywhere in M01 — its existence must not leak.
                clientId = await customers.ResolveClientIdAsync(tenantId, requested, ct);
                if (clientId is null || !await customers.CanActorAccessClientAsync(tenantId, clientId.Value, currentUser.Id, ct))
                    return Result.Fail<Guid>("CLIENT_NOT_FOUND");
            }

            if (participantIds.Count > 0)
            {
                var known = await admin.GetUserNamesAsync(tenantId, participantIds, ct);
                if (participantIds.Any(id => !known.ContainsKey(id)))
                    return Result.Fail<Guid>("APPOINTMENT_PARTICIPANT_UNKNOWN");
            }

            suppressed.Complete();
        }

        var scheduled = Appointment.Schedule(
            tenantId, currentUser.Id, req.Title, req.StartAt, req.DurationMinutes, req.TimeZone,
            req.Mode, req.Location, req.MeetingUrl, req.LeadId, clientId, participantIds,
            req.Description, clock.GetUtcNow());
        if (scheduled.IsFailure) return Result.Fail<Guid>(scheduled.Error!);

        var appointment = scheduled.Value!;
        db.Appointments.Add(appointment);

        if (appointment.ParticipantIds.Count > 0)
            await publisher.PublishAsync(
                new AppointmentScheduledEvent(tenantId, appointment.Id, appointment.ParticipantIds.ToList()), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok(appointment.Id);
    }
}
