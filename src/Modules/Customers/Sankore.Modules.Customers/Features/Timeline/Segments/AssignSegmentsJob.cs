namespace Sankore.Modules.Customers.Features.Timeline.Segments;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegments;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — one tenant per invocation (US-M01-BE-27).
///
/// The argument is an opaque identifier, nothing else: Hangfire serializes job arguments into
/// its own storage, so a payload carrying business data would end up persisted outside the
/// module's schema and outside its encryption.
/// </summary>
public sealed class AssignSegmentsJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        // No HTTP context: install the tenant plus the SYSTEM actor (Guid.Empty) so the
        // AuditBehavior attributes the reclassification to the system account.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AssignSegmentsJob>>();

        var result = await sender.Send(new AssignSegmentsCommand(tenantId), CancellationToken.None);

        if (result.IsFailure)
        {
            logger.LogError(
                "Segment assignment failed for tenant {TenantId}: {Error}", tenantId, result.Error);
            return;
        }

        logger.LogInformation(
            "Segment assignment done for tenant {TenantId}: {Changed}/{Evaluated} client(s) reclassified.",
            tenantId, result.Value.SegmentsChanged, result.Value.ClientsEvaluated);
    }
}
