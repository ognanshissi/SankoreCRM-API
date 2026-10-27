namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Chases the projects whose CRITICAL close date has passed (<see cref="ProjectTemplate.CriticalCloseDate"/>)
/// — a crop loan granted after the rains have started is a lost sale. Nightly, per tenant.
///
/// <para>
/// <b>One urgent task per passed date</b>, for whoever follows the project (its owner, else the
/// lead's agent): a task created after the date passed means the project was already chased, so
/// moving the date forward and letting it pass again raises a new one, and a project nobody
/// moved does not pile up a task a night.
/// </para>
///
/// <para>
/// <b>A project with no date at all</b> — created before its template made the date critical —
/// cannot be late, so it gets one task to set the deadline, once.
/// </para>
/// </summary>
public sealed class ProjectCloseDateOverdueJob(IServiceScopeFactory scopeFactory, ILogger<ProjectCloseDateOverdueJob> logger)
{
    public const string RecurringJobPrefix = "project-close-date-overdue";
    public const string CronExpression = "0 6 * * *";

    /// <summary>The task's trigger: <c>TriggerEventId</c> is the project's id.</summary>
    internal const string Trigger = "ProjectCloseDateOverdue";

    /// <summary>The task asking for the missing deadline: <c>TriggerEventId</c> is the project's id.</summary>
    internal const string MissingTrigger = "ProjectCloseDateMissing";

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var created = await RunAsync(
            sp.GetRequiredService<LeadsDbContext>(),
            sp.GetRequiredService<ProjectTemplateResolver>(),
            sp.GetRequiredService<TimeProvider>(),
            CancellationToken.None);

        if (created > 0)
            logger.LogInformation("{Count} overdue critical close date(s) chased for tenant {TenantId}.", created, tenantId);
    }

    /// <summary>Creates the missing tasks and returns how many.</summary>
    internal static async Task<int> RunAsync(
        LeadsDbContext db, ProjectTemplateResolver templates, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var late = await db.Opportunities.AsNoTracking()
            .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost
                     && (o.ExpectedCloseDate == null || o.ExpectedCloseDate < now))
            .ToListAsync(ct);
        if (late.Count == 0)
            return 0;

        var ids = late.Select(o => o.Id).ToList();
        var chased = await db.CrmTasks
            .Where(t => (t.TriggerEventType == Trigger || t.TriggerEventType == MissingTrigger)
                     && t.TriggerEventId != null && ids.Contains(t.TriggerEventId.Value))
            .Select(t => new { ProjectId = t.TriggerEventId!.Value, t.TriggerEventType, t.CreatedAt })
            .ToListAsync(ct);

        var created = 0;
        foreach (var project in late)
        {
            var template = await templates.ForProjectAsync(project, ct);
            if (!template.CriticalCloseDate || !template.TracksForecast)
                continue;

            var agent = project.OwnerId
                ?? await db.Leads.Where(l => l.Id == project.LeadId).Select(l => l.CurrentAssignedId).FirstOrDefaultAsync(ct);

            if (project.ExpectedCloseDate is not { } closeDate)
            {
                if (chased.Any(c => c.ProjectId == project.Id && c.TriggerEventType == MissingTrigger))
                    continue;

                Add(db, CrmTask.Create(
                    tenantId:         project.TenantId,
                    type:             CrmTaskType.Generic,
                    priority:         CrmTaskPriority.High,
                    title:            Truncate($"Date limite à fixer : {project.Title}", CrmTask.TitleMaxLength),
                    dueAt:            now,
                    leadId:           project.LeadId,
                    assignedAgentId:  agent,
                    description:      $"Le projet « {project.Title} » ({project.Product}) n'a pas de date de clôture, alors que "
                                    + "ce produit en fait une échéance. Fixez-la pour que le projet soit relancé à temps.",
                    triggerEventType: MissingTrigger,
                    triggerEventId:   project.Id), project);
                created++;
                continue;
            }

            if (chased.Any(c => c.ProjectId == project.Id && c.TriggerEventType == Trigger && c.CreatedAt >= closeDate))
                continue;

            var task = CrmTask.Create(
                tenantId:         project.TenantId,
                type:             CrmTaskType.Generic,
                priority:         CrmTaskPriority.Critical,
                title:            Truncate($"Clôture dépassée : {project.Title}", CrmTask.TitleMaxLength),
                dueAt:            now,
                leadId:           project.LeadId,
                assignedAgentId:  agent,
                description:      $"La date de clôture prévue du projet « {project.Title} » ({project.Product}) était le "
                                + $"{closeDate:dd/MM/yyyy}. Pour ce produit, un accord tardif ne sert plus : relancez le client "
                                + "aujourd'hui, ou clôturez le projet s'il est perdu.",
                triggerEventType: Trigger,
                triggerEventId:   project.Id);

            Add(db, task, project);
            created++;
        }

        if (created > 0)
            await db.SaveChangesAsync(ct);
        return created;
    }

    private static void Add(LeadsDbContext db, CrmTask task, Opportunity project)
    {
        if (project.LeadId is null && project.CustomerEntityId is { } clientId)
            task.AboutClient(clientId);
        db.CrmTasks.Add(task);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
