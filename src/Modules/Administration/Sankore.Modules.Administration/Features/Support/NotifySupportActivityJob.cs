namespace Sankore.Modules.Administration.Features.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Infrastructure.Support;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Tells requesters what the platform did on their tickets. The platform answers in the registry,
/// which has no mail engine and no route back to this host, so this host reads it: every five
/// minutes, each tenant's platform replies and status changes of the last <see cref="Lookback"/>.
///
/// <para>
/// <b>Nothing is stored to remember what was already sent</b> — the same bargain as
/// <c>NotifyTenantModuleDecisionsJob</c>: M08's outbox and the bell both drop a key they have seen,
/// and the key is the registry's message id.
/// </para>
///
/// <para>
/// A reply sent with a status change is ONE notification, not two: the registry writes both
/// entries at the same instant, and the requester wants the answer, not a second mail saying the
/// ticket now waits on them.
/// </para>
/// </summary>
public sealed class NotifySupportActivityJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    TimeProvider clock,
    ILogger<NotifySupportActivityJob> logger)
{
    public const string RecurringJobId = "support-activity";
    public const string CronExpression = "*/5 * * * *";

    internal static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);
        foreach (var tenant in tenants)
            await ExecuteForTenantAsync(tenant.Id, CancellationToken.None);
    }

    internal async Task<int> ExecuteForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISupportRegistry>();
        var notifier = scope.ServiceProvider.GetRequiredService<SupportNotifier>();

        var answer = await registry.ActivityAsync(tenantId, clock.GetUtcNow() - Lookback, ct);
        if (!answer.IsSuccess)
        {
            // A silent registry is not a reason to fail the run for every other tenant.
            logger.LogWarning("Support activity of tenant {TenantId} not read: {Error}", tenantId, answer.Error);
            return 0;
        }

        var notified = 0;
        foreach (var act in answer.Value!.GroupBy(a => (a.TicketId, a.CreatedAt)))
        {
            var reply = act.FirstOrDefault(a => a.Kind == SupportMessageKind.Reply);
            var change = act.LastOrDefault(a => a.Kind == SupportMessageKind.StatusChange);
            var first = reply ?? change!;

            // A reply that left the ticket waiting on the tenant says so by being a reply.
            if (await notifier.PlatformActedAsync(tenantId, first.MessageId, first.TicketId, first.Reference,
                    first.Subject, first.RequesterUserId, first.RequesterEmail, first.AuthorName,
                    reply?.Body, change?.Status, ct))
                notified++;
        }

        return notified;
    }
}
