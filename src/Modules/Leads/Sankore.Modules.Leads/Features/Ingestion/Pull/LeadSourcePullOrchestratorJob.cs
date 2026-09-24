namespace Sankore.Modules.Leads.Features.Ingestion.Pull;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NCrontab;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job (every minute, all tenants).
/// Scans Active ScheduledPull sources whose cron is due and no run is in progress,
/// then enqueues a PullLeadSourceJob per source on the "lead-pull" queue.
/// </summary>
public sealed class LeadSourcePullOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore)
{
    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            using var bgCtx = BackgroundJobContext.SetScope(tenant.Id, Guid.Empty, "SYSTEM");
            using var scope = scopeFactory.CreateScope();

            var db     = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
            var hangfire = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            var clock  = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<LeadSourcePullOrchestratorJob>>();

            var now = clock.GetUtcNow();

            // Find Active ScheduledPull sources for this tenant
            var sources = await db.LeadSourceConfigs
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenant.Id
                         && s.Mode == IntegrationMode.ScheduledPull
                         && s.Status == LeadSourceStatus.Active)
                .ToListAsync();

            // Get source IDs that have a Running run (in-progress lock)
            var runningSourceIds = await db.LeadSourceRuns
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenant.Id
                         && r.Status == LeadSourceRunStatus.Running)
                .Select(r => r.SourceId)
                .ToHashSetAsync();

            foreach (var source in sources)
            {
                // Skip if already running
                if (runningSourceIds.Contains(source.Id))
                    continue;

                // Check cron schedule
                if (!IsCronDue(source, now))
                    continue;

                // Enqueue the pull job
                hangfire.Enqueue<PullLeadSourceJob>(
                    j => j.ExecuteAsync(source.Id, tenant.Id));

                logger.LogDebug(
                    "Enqueued pull for source {SourceId} ({Code}) tenant {TenantId}",
                    source.Id, source.Code, tenant.Id);
            }
        }
    }

    private static bool IsCronDue(LeadSourceConfig source, DateTimeOffset now)
    {
        var settings = source.Settings as ScheduledPullSettings;
        if (settings is null) return false;

        try
        {
            var schedule = CrontabSchedule.Parse(settings.CronSchedule);
            var lastPull = source.LastPullAt?.UtcDateTime ?? source.CreatedAt.UtcDateTime;
            var nextOccurrence = schedule.GetNextOccurrence(lastPull);
            return now.UtcDateTime >= nextOccurrence;
        }
        catch
        {
            return false;
        }
    }
}
