namespace Sankore.Modules.Leads.Features.Appointments;

using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Mails the invited colleagues: the invitation when an appointment is planned, the notice when it
/// is moved or cancelled. Through M12, which owns each user's address and language.
///
/// <para>
/// Dates are written in the ORGANISER's time zone, which the appointment carries, and the zone is
/// named in the mail: the server is in UTC and no user zone is exposed across modules. In French,
/// like every platform template's variables — the template text itself follows the recipient's
/// language.
/// </para>
///
/// <para>
/// The idempotency key is the appointment and the recipient: a redelivered event mails nobody
/// twice, while an invitation and a cancellation are two different mails.
/// </para>
/// </summary>
public sealed class AppointmentInvitationConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<AppointmentInvitationConsumer> logger)
    : IConsumer<AppointmentScheduledEvent>, IConsumer<AppointmentCancelledEvent>, IConsumer<AppointmentRescheduledEvent>
{
    public const string InvitationTemplateKey = "appointment.invitation";
    public const string CancellationTemplateKey = "appointment.cancelled";
    public const string RescheduleTemplateKey = "appointment.rescheduled";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public Task Consume(ConsumeContext<AppointmentScheduledEvent> context)
        => NotifyAsync(context.Message.TenantId, context.Message.AppointmentId, context.Message.ParticipantIds,
            InvitationTemplateKey, "appt-invite", context.CancellationToken);

    public Task Consume(ConsumeContext<AppointmentCancelledEvent> context)
        => NotifyAsync(context.Message.TenantId, context.Message.AppointmentId, context.Message.ParticipantIds,
            CancellationTemplateKey, "appt-cancel", context.CancellationToken);

    // Keyed on the new start too: each move is its own mail, a redelivery of one is not.
    public Task Consume(ConsumeContext<AppointmentRescheduledEvent> context)
        => NotifyAsync(context.Message.TenantId, context.Message.AppointmentId, context.Message.ParticipantIds,
            RescheduleTemplateKey, $"appt-move-{context.Message.StartAt.UtcTicks}", context.CancellationToken);

    private async Task NotifyAsync(
        Guid tenantId, Guid appointmentId, IReadOnlyList<Guid> participantIds,
        string templateKey, string keyPrefix, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || participantIds.Count == 0) return;

        // The ambient scope first: ITenantContext is built from it, and a consumer has no request.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();
        var admin = sp.GetRequiredService<IAdministrationModule>();
        var customers = sp.GetRequiredService<ICustomersModule>();

        var appointment = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
        {
            logger.LogWarning("Appointment {AppointmentId} not found: no invitation sent", appointmentId);
            return;
        }

        var data = await BuildDataAsync(db, admin, customers, appointment, ct);

        foreach (var userId in participantIds.Distinct())
        {
            var sent = await admin.NotifyUserAsync(
                new NotifyUserRequest(
                    TenantId: tenantId,
                    UserId: userId,
                    TemplateKey: templateKey,
                    IdempotencyKey: $"{keyPrefix}-{appointment.Id}-{userId}",
                    TemplateData: data),
                ct);
            if (!sent)
                logger.LogWarning(
                    "No {TemplateKey} mail for appointment {AppointmentId}: user {UserId} is unknown or has no address",
                    templateKey, appointment.Id, userId);
        }
    }

    internal static async Task<Dictionary<string, object>> BuildDataAsync(
        LeadsDbContext db, IAdministrationModule admin, ICustomersModule customers,
        Appointment appointment, CancellationToken ct)
    {
        var zone = Appointment.ResolveTimeZone(appointment.TimeZone);
        var start = TimeZoneInfo.ConvertTime(appointment.StartAt, zone);
        var end = TimeZoneInfo.ConvertTime(appointment.EndAt, zone);

        var organizer = (await admin.GetUserNamesAsync(appointment.TenantId, [appointment.OrganizerId], ct))
            .GetValueOrDefault(appointment.OrganizerId) ?? "";

        var subject = "";
        if (appointment.LeadId is { } leadId)
        {
            var leadName = await db.Leads.AsNoTracking().Where(l => l.Id == leadId).Select(l => l.FullName).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(leadName)) subject = $"Lead : {leadName}";
        }
        else if (appointment.ClientId is { } clientId)
        {
            var client = await customers.GetClientSummaryAsync(appointment.TenantId, clientId, ct);
            if (client is not null) subject = $"Client : {client.DisplayName}";
        }

        return new Dictionary<string, object>
        {
            ["appointment_title"] = appointment.Title,
            ["appointment_date"] = start.ToString("dddd d MMMM yyyy", French),
            ["appointment_time"] = $"{start:HH\\:mm} – {end:HH\\:mm}",
            ["time_zone"] = zone.Id,
            ["duration_minutes"] = appointment.DurationMinutes,
            ["organizer_name"] = organizer,
            ["mode_label"] = ModeLabel(appointment),
            ["location"] = appointment.Location ?? "",
            ["meeting_url"] = appointment.MeetingUrl ?? "",
            ["subject_label"] = subject,
            ["description"] = appointment.Description ?? "",
            ["cancellation_reason"] = appointment.CancellationReason ?? "",
        };
    }

    private static string ModeLabel(Appointment a) => a.Mode switch
    {
        AppointmentMode.Online => a.MeetingProvider switch
        {
            MeetingProvider.Zoom => "En ligne · Zoom",
            MeetingProvider.Teams => "En ligne · Microsoft Teams",
            MeetingProvider.GoogleMeet => "En ligne · Google Meet",
            _ => "En ligne",
        },
        _ => "Présentiel",
    };
}
