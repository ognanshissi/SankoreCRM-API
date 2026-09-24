namespace Sankore.Modules.Leads.Features.Ingestion.Pull.ManualPull;

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ManualPullHandler(
    LeadsDbContext db,
    IBackgroundJobClient hangfire)
    : IRequestHandler<ManualPullCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ManualPullCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail<Guid>("SOURCE_NOT_FOUND");

        if (source.Status != LeadSourceStatus.Active)
            return Result.Fail<Guid>("SOURCE_NOT_ACTIVE");

        if (source.Mode != IntegrationMode.ScheduledPull)
            return Result.Fail<Guid>("SOURCE_NOT_PULL_MODE");

        // Check no run in progress
        var hasRunning = await db.LeadSourceRuns
            .AnyAsync(r => r.SourceId == source.Id
                        && r.Status == LeadSourceRunStatus.Running, ct);

        if (hasRunning)
            return Result.Fail<Guid>("RUN_ALREADY_IN_PROGRESS");

        // Enqueue the pull job
        var jobId = hangfire.Enqueue<PullLeadSourceJob>(
            j => j.ExecuteAsync(source.Id, source.TenantId));

        return Result.Ok(Guid.Parse(jobId));
    }
}
