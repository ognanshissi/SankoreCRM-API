namespace Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — one tenant's duplicate detection run (US-M01-BE-24).
/// <para>
/// Its only argument is an opaque tenant identifier: the Hangfire tables are a queue, not a place
/// for client data, so nothing personal is ever serialized into a job payload.
/// </para>
/// <para>
/// Establishes the SYSTEM identity first (<see cref="BackgroundJobContext.SetScope"/> with
/// <see cref="Guid.Empty"/> as the user id), then goes through MediatR so the command travels the
/// normal pipeline — validation, transaction, audit. The audit row therefore names SYSTEM as the
/// actor, which is what distinguishes a nightly detection from a human review.
/// </para>
/// </summary>
public sealed class DetectDuplicatesJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DetectDuplicatesJob>>();

        var result = await sender.Send(new DetectDuplicatesCommand(tenantId), CancellationToken.None);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Duplicate detection failed for tenant {TenantId}: {Error}", tenantId, result.Error);
            return;
        }

        logger.LogInformation(
            "Duplicate detection done for tenant {TenantId}: {Created} new candidate(s), {Refreshed} refreshed.",
            tenantId, result.Value.CandidatesCreated, result.Value.CandidatesRefreshed);

        // A run that dropped pairs still reports IsSuccess, so without this the Hangfire log would
        // read as a clean sweep. Whoever is watching the job needs to see the gap.
        if (result.Value.CandidatesFailed > 0)
        {
            logger.LogWarning(
                "Duplicate detection for tenant {TenantId} skipped {Failed} pair(s) on a domain error; "
                + "the handler logged each pair's client ids.",
                tenantId, result.Value.CandidatesFailed);
        }
    }
}
