namespace Sankore.Modules.Customers.Features.Timeline.Loyalty;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScores;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — one tenant per invocation (US-M01-BE-28). Runs after the segmentation job so
/// a client scored tonight is already in its final segment.
/// </summary>
public sealed class ComputeLoyaltyScoresJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ComputeLoyaltyScoresJob>>();

        var result = await sender.Send(new ComputeLoyaltyScoresCommand(tenantId), CancellationToken.None);

        if (result.IsFailure)
        {
            logger.LogError(
                "Loyalty scoring failed for tenant {TenantId}: {Error}", tenantId, result.Error);
            return;
        }

        logger.LogInformation(
            "Loyalty scoring done for tenant {TenantId}: {Count} client(s), {Provisional} provisional.",
            tenantId, result.Value.ClientsScored, result.Value.ProvisionalCount);
    }
}
