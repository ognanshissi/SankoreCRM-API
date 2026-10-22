namespace Sankore.Modules.Leads.Features.Tasks.Collaboration;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

public enum TaskActivityKind
{
    /// <summary>The task was created for, or handed to, the recipients.</summary>
    Assigned,

    /// <summary>The recipients were associated with the task.</summary>
    Associated,

    Commented,

    FileAttached,
}

/// <summary>
/// Something happened on a task that its people should know. Written to M13's outbox with the
/// change, so a rolled-back write tells nobody; <paramref name="Marker"/> makes the bell's
/// idempotency key — the comment or attachment id, the creation instant.
/// </summary>
public sealed record CrmTaskActivityEvent(
    Guid TenantId,
    Guid TaskId,
    TaskActivityKind Kind,
    Guid ActorId,
    IReadOnlyList<Guid> RecipientIds,
    string Marker) : IntegrationEventBase;

/// <summary>
/// The bell (M08) for task activity: who did what, on which task, with a link to it. The SUBJECT
/// of the task is never named — a notification is read on a locked screen — and a comment is
/// announced, not quoted.
/// </summary>
public sealed class CrmTaskActivityConsumer(IServiceScopeFactory scopeFactory, ILogger<CrmTaskActivityConsumer> logger)
    : IConsumer<CrmTaskActivityEvent>
{
    public const string Category = "task.activity";

    public async Task Consume(ConsumeContext<CrmTaskActivityEvent> context)
    {
        var e = context.Message;
        var ct = context.CancellationToken;
        if (e.TenantId == Guid.Empty || e.RecipientIds.Count == 0) return;

        using var bgCtx = BackgroundJobContext.SetScope(e.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var title = await sp.GetRequiredService<LeadsDbContext>().CrmTasks
            .AsNoTracking()
            .Where(t => t.Id == e.TaskId)
            .Select(t => t.Title)
            .FirstOrDefaultAsync(ct);
        if (title is null) return;

        var names = await sp.GetRequiredService<IAdministrationModule>().GetUserNamesAsync(e.TenantId, [e.ActorId], ct);
        var actor = names.GetValueOrDefault(e.ActorId) ?? "Un collègue";

        var (heading, body) = e.Kind switch
        {
            TaskActivityKind.Assigned => ($"Nouvelle tâche : {title}", $"{actor} vous l'a confiée."),
            TaskActivityKind.Associated => ($"Vous êtes associé à « {title} »", $"{actor} vous a ajouté à la tâche."),
            TaskActivityKind.Commented => ($"Nouveau commentaire sur « {title} »", $"{actor} a commenté."),
            TaskActivityKind.FileAttached => ($"Nouveau fichier sur « {title} »", $"{actor} a joint un fichier."),
            _ => (title, actor),
        };

        var notifications = sp.GetRequiredService<INotificationsModule>();

        foreach (var recipient in e.RecipientIds.Distinct())
        {
            try
            {
                await notifications.NotifyInAppAsync(
                    new InAppNotificationRequest(
                        e.TenantId, recipient, Category, heading, body, $"/tasks/{e.TaskId}",
                        $"task-{e.Kind}-{e.TaskId}-{e.Marker}-{recipient}"),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Task {TaskId}: in-app notification to {UserId} failed.", e.TaskId, recipient);
            }
        }
    }
}
