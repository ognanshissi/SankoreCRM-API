namespace Sankore.Modules.Administration.Features.PaidModules;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Tells requesters what the platform decided (lot 4). The decision is taken in the registry, which
/// has no mail engine and no route back to this host, so this host reads it: every five minutes,
/// each tenant's subscriptions decided in the last <see cref="Lookback"/>.
///
/// <para>
/// <b>Nothing is stored to remember what was already sent.</b> M08's outbox drops an idempotency
/// key it has seen and the bell does the same per user, both keyed on the subscription id, so a
/// decision is announced once however many runs see it. The look-back only bounds the work: it is
/// far longer than any plausible outage of this job, and a decision older than it is not news.
/// </para>
/// </summary>
public sealed class NotifyTenantModuleDecisionsJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    TimeProvider clock,
    ILogger<NotifyTenantModuleDecisionsJob> logger)
{
    public const string RecurringJobId = "tenant-module-decisions";
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
        var registry = scope.ServiceProvider.GetRequiredService<ITenantModuleRegistry>();
        var notifier = scope.ServiceProvider.GetRequiredService<TenantModuleNotifier>();

        var answer = await registry.ListAsync(tenantId, ct);
        if (!answer.IsSuccess)
        {
            // A silent registry is not a reason to fail the run for every other tenant.
            logger.LogWarning("Module decisions of tenant {TenantId} not read: {Error}", tenantId, answer.Error);
            return 0;
        }

        var since = clock.GetUtcNow() - Lookback;
        var notified = 0;

        foreach (var module in answer.Value!.Modules)
        foreach (var row in module.History)
        {
            // Only what a tenant ASKED for: a direct activation or a migrated row has nobody waiting.
            if (row.Source != "Request" || row.DecidedAt is not { } decidedAt || decidedAt < since
                || string.IsNullOrWhiteSpace(row.RequestedByEmail))
                continue;

            // An approved request may already have been deactivated since; it was still approved.
            bool? approved = row.Status switch
            {
                "Rejected" => false,
                "Active" or "Deactivated" when row.ActivatedAt is not null => true,
                _ => null,
            };
            if (approved is null) continue;

            if (await notifier.DecidedAsync(tenantId, row.Id, row.ModuleCode, approved.Value,
                    row.RequestedByEmail, row.AcceptedMonthlyPrice, row.AcceptedCurrency, row.DecisionReason, ct))
                notified++;
        }

        return notified;
    }
}
