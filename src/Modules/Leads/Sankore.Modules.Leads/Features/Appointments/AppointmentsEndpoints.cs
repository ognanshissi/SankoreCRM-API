namespace Sankore.Modules.Leads.Features.Appointments;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

/// <summary>
/// Rendez-vous planned from « Ma journée ». Creating one is logging a future interaction, so it
/// takes the same permission as logging an activity — agents hold it already, and an appointment
/// with nobody's lead is still part of the commercial work this permission covers.
/// </summary>
public static class AppointmentsEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("appointments");

        // POST /appointments
        group.MapPost("", async (ScheduleAppointmentRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ScheduleAppointmentCommand(req), ct);
                return result.IsSuccess
                    ? Results.Created($"appointments/{result.Value}", result.Value)
                    : result.Error is "LEAD_NOT_FOUND" or "CLIENT_NOT_FOUND"
                        ? Results.Problem(title: "Schedule appointment failed", detail: result.Error, statusCode: 404)
                        : Results.Problem(title: "Schedule appointment failed", detail: result.Error, statusCode: 422);
            })
            .WithName("ScheduleAppointment")
            .WithTags("Appointments")
            .RequireAuthorization(Permissions.CanLogLeadActivity.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /appointments/{id}/cancel
        group.MapPost("{appointmentId:guid}/cancel", async (Guid appointmentId, CancelAppointmentRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new CancelAppointmentCommand(appointmentId, req.Reason), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : result.Error == "APPOINTMENT_NOT_FOUND"
                        ? Results.NotFound()
                        : Results.Problem(title: "Cancel appointment failed", detail: result.Error, statusCode: 422);
            })
            .WithName("CancelAppointment")
            .WithTags("Appointments")
            .RequireAuthorization(Permissions.CanLogLeadActivity.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /appointments/{id}/reschedule
        group.MapPost("{appointmentId:guid}/reschedule", async (Guid appointmentId, RescheduleAppointmentRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new RescheduleAppointmentCommand(appointmentId, req.StartAt, req.DurationMinutes), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : result.Error == "APPOINTMENT_NOT_FOUND"
                        ? Results.NotFound()
                        : Results.Problem(title: "Reschedule appointment failed", detail: result.Error, statusCode: 422);
            })
            .WithName("RescheduleAppointment")
            .WithTags("Appointments")
            .RequireAuthorization(Permissions.CanLogLeadActivity.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }
}
