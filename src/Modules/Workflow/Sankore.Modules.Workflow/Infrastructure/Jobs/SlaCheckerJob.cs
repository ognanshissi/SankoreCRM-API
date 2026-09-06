using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Jobs;

/// <summary>
/// Background service that runs every minute and times out workflow steps
/// whose SLA deadline (DueAt) has passed without an approver decision.
/// Uses IgnoreQueryFilters() because it runs outside any HTTP/tenant context.
/// </summary>
internal sealed class SlaCheckerJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SlaCheckerJob> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SlaCheckerJob started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);

            try
            {
                await CheckSlaDeadlinesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "SlaCheckerJob encountered an error during SLA check.");
            }
        }
    }

    private async Task CheckSlaDeadlinesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = DateTimeOffset.UtcNow;

        // Find all instance IDs where the active step has exceeded its DueAt.
        // IgnoreQueryFilters: no HTTP context in a background job — tenant filter skipped intentionally.
        var timedOutInstanceIds = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.Status == StepStatus.AwaitingApproval
                     && s.DueAt.HasValue
                     && s.DueAt.Value <= now)
            .Select(s => s.InstanceId)
            .Distinct()
            .ToListAsync(ct);

        if (timedOutInstanceIds.Count == 0)
            return;

        logger.LogInformation(
            "SlaCheckerJob: {Count} workflow instance(s) have exceeded their SLA deadline.",
            timedOutInstanceIds.Count);

        foreach (var instanceId in timedOutInstanceIds)
        {
            try
            {
                await TimeoutInstanceAsync(db, instanceId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SlaCheckerJob: failed to time out workflow instance {InstanceId}.", instanceId);
            }
        }
    }

    private static async Task TimeoutInstanceAsync(
        WorkflowDbContext db, Guid instanceId, CancellationToken ct)
    {
        // Load the instance with its steps using tracking so EF picks up domain mutations.
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);

        if (instance is null
            || instance.Status is WorkflowStatus.Completed
                or WorkflowStatus.Rejected
                or WorkflowStatus.Cancelled
                or WorkflowStatus.TimedOut)
            return;

        instance.TimeoutCurrentStep();
        await db.SaveChangesAsync(ct);
    }
}
