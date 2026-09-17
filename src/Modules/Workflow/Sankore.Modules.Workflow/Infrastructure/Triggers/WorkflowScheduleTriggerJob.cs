using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sankore.Modules.Workflow.Infrastructure.Triggers;

/// <summary>
/// Background service placeholder for schedule-based trigger evaluation.
/// Full cron scheduling is a future phase — this stub keeps the composition
/// root stable and can be extended without touching <c>WorkflowModule.cs</c>.
/// </summary>
internal sealed class WorkflowScheduleTriggerJob(
    ILogger<WorkflowScheduleTriggerJob> logger
) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "WorkflowScheduleTriggerJob started (stub — schedule triggers not yet implemented).");
        return Task.CompletedTask;
    }
}
