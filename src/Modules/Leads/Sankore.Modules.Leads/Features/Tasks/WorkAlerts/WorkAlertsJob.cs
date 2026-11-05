namespace Sankore.Modules.Leads.Features.Tasks.WorkAlerts;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// The bell for what comes due (2026-11-05, product owner): a task past its deadline, then its
/// holder's manager a day later; an appointment shortly before it starts; a reminder as it falls due.
/// Before, a task could go late, and an appointment come and go, with nobody told.
///
/// <para>
/// Every alert is keyed on the date it announces: moving a task or an appointment announces the new
/// date, and a run that finds nothing new sends nothing. The look-back windows are what keep a first
/// run from ringing for everything that was ever late.
/// </para>
/// </summary>
public sealed class WorkAlertsJob(IServiceScopeFactory scopeFactory, ILogger<WorkAlertsJob> logger)
{
    public const string RecurringJobPrefix = "work-alerts";
    public const string CronExpression = "*/5 * * * *";

    public const string TaskOverdueCategory = "task.overdue";
    public const string AppointmentCategory = "appointment.reminder";
    public const string ReminderCategory = "lead.reminder";

    internal static readonly TimeSpan TaskLookBack = TimeSpan.FromHours(48);
    internal static readonly TimeSpan ManagerAfter = TimeSpan.FromHours(24);
    internal static readonly TimeSpan ManagerLookBack = TimeSpan.FromHours(72);
    internal static readonly TimeSpan AppointmentAhead = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ReminderAhead = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ReminderLookBack = TimeSpan.FromHours(24);

    private const int MaxRowsPerKind = 500;
    private const int MaxTitle = 150;

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var sent = await RunAsync(
            tenantId,
            sp.GetRequiredService<LeadsDbContext>(),
            sp.GetRequiredService<INotificationsModule>(),
            sp.GetRequiredService<IAdministrationModule>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            CancellationToken.None);

        if (sent > 0)
            logger.LogInformation("{Count} work alert(s) sent for tenant {TenantId}.", sent, tenantId);
    }

    /// <summary>Sends what has not been sent yet and returns how many.</summary>
    internal static async Task<int> RunAsync(
        Guid tenantId,
        LeadsDbContext db,
        INotificationsModule notifications,
        IAdministrationModule administration,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<InAppNotificationRequest> alerts =
        [
            .. await TaskAlertsAsync(tenantId, db, administration, now, ct),
            .. await AppointmentAlertsAsync(tenantId, db, now, ct),
            .. await ReminderAlertsAsync(tenantId, db, now, ct),
        ];
        if (alerts.Count == 0) return 0;

        // One read for the whole run: most of these were already sent by an earlier one.
        var sent = await notifications.FindInAppKeysAsync(
            tenantId,
            alerts.Select(a => a.UserId).Distinct().ToList(),
            alerts.Select(a => a.IdempotencyKey).ToList(),
            ct);

        var count = 0;
        foreach (var alert in alerts.Where(a => !sent.Contains(a.IdempotencyKey)))
        {
            try
            {
                if ((await notifications.NotifyInAppAsync(alert, ct)).IsSuccess) count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Work alert {Key} could not be sent.", alert.IdempotencyKey);
            }
        }
        return count;
    }

    private static async Task<List<InAppNotificationRequest>> TaskAlertsAsync(
        Guid tenantId, LeadsDbContext db, IAdministrationModule administration, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - ManagerLookBack;
        var late = await db.CrmTasks.AsNoTracking()
            .Where(t => (t.Status == CrmTaskStatus.Pending || t.Status == CrmTaskStatus.InProgress)
                     && t.AssignedAgentId != null
                     && (t.SlaDeadline ?? t.DueAt) < now
                     && (t.SlaDeadline ?? t.DueAt) > since)
            .OrderByDescending(t => t.SlaDeadline ?? t.DueAt)
            .Take(MaxRowsPerKind)
            .Select(t => new { t.Id, t.Title, Holder = t.AssignedAgentId!.Value, Deadline = t.SlaDeadline ?? t.DueAt })
            .ToListAsync(ct);

        var alerts = new List<InAppNotificationRequest>();

        foreach (var task in late.Where(t => now - t.Deadline < TaskLookBack))
            alerts.Add(new InAppNotificationRequest(
                tenantId, task.Holder, TaskOverdueCategory,
                Clip($"Tâche en retard : {task.Title}"),
                "Son échéance est passée.",
                $"/tasks/{task.Id}",
                $"task-overdue-{task.Id}-{task.Deadline.ToUnixTimeSeconds()}-{task.Holder}"));

        var forManagers = late.Where(t => now - t.Deadline >= ManagerAfter).ToList();
        if (forManagers.Count == 0) return alerts;

        var holders = forManagers.Select(t => t.Holder).Distinct().ToList();
        var names = await administration.GetUserNamesAsync(tenantId, holders, ct);
        var managers = new Dictionary<Guid, Guid?>();
        foreach (var holder in holders)
        {
            var supervisors = await administration.GetUserSupervisorsAsync(tenantId, holder, ct);
            managers[holder] = supervisors.LineManagerId ?? supervisors.AgencyManagerId;
        }

        foreach (var task in forManagers)
        {
            if (managers[task.Holder] is not { } manager || manager == task.Holder) continue;
            var who = names.GetValueOrDefault(task.Holder) ?? "Un collaborateur";
            alerts.Add(new InAppNotificationRequest(
                tenantId, manager, TaskOverdueCategory,
                Clip($"Tâche en retard depuis plus d'un jour : {task.Title}"),
                $"{who} ne l'a pas encore traitée.",
                $"/tasks/{task.Id}",
                $"task-overdue-manager-{task.Id}-{task.Deadline.ToUnixTimeSeconds()}-{manager}"));
        }
        return alerts;
    }

    private static async Task<List<InAppNotificationRequest>> AppointmentAlertsAsync(
        Guid tenantId, LeadsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var until = now + AppointmentAhead;
        var soon = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Scheduled && a.StartAt > now && a.StartAt <= until)
            .OrderBy(a => a.StartAt)
            .Take(MaxRowsPerKind)
            .ToListAsync(ct);

        return soon
            .SelectMany(a => a.ParticipantIds.Append(a.OrganizerId).Distinct().Select(person =>
                new InAppNotificationRequest(
                    tenantId, person, AppointmentCategory,
                    Clip($"Rendez-vous : {a.Title}"),
                    $"Il commence dans {InMinutes(a.StartAt - now)}.",
                    "/tasks/calendar",
                    $"appointment-reminder-{a.Id}-{a.StartAt.ToUnixTimeSeconds()}-{person}")))
            .ToList();
    }

    /// <summary>The same people as the agenda: whoever set the reminder, and whoever holds the lead.</summary>
    private static async Task<List<InAppNotificationRequest>> ReminderAlertsAsync(
        Guid tenantId, LeadsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - ReminderLookBack;
        var until = now + ReminderAhead;
        var due = await db.LeadReminders.AsNoTracking()
            .Where(r => r.Status == ReminderStatus.Pending && r.DueAt > since && r.DueAt <= until)
            .Join(db.Leads, r => r.LeadId, l => l.Id, (r, l) => new
            {
                r.Id, r.LeadId, r.Title, r.DueAt, r.CreatedBy,
                Lead = l.FullName,
                Holder = l.CurrentAssignedId ?? l.OwnerId,
            })
            .OrderBy(r => r.DueAt)
            .Take(MaxRowsPerKind)
            .ToListAsync(ct);

        return due
            .SelectMany(r => new[] { r.CreatedBy, r.Holder ?? Guid.Empty }
                .Where(person => person != Guid.Empty)
                .Distinct()
                .Select(person => new InAppNotificationRequest(
                    tenantId, person, ReminderCategory,
                    Clip($"Rappel : {r.Title}"),
                    r.DueAt > now ? $"{r.Lead}, dans {InMinutes(r.DueAt - now)}." : $"{r.Lead}, à faire maintenant.",
                    $"/leads/{r.LeadId}",
                    $"lead-reminder-{r.Id}-{r.DueAt.ToUnixTimeSeconds()}-{person}")))
            .ToList();
    }

    private static string InMinutes(TimeSpan delay)
    {
        var minutes = Math.Max(1, (int)Math.Round(delay.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static string Clip(string title) => title.Length <= MaxTitle ? title : title[..(MaxTitle - 1)] + "…";
}
