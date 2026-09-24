namespace Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — replays a failed/rejected ingestion by re-parsing the raw payload
/// and dispatching it through the standard CaptureLeadCommand pipeline.
/// Runs under SYSTEM identity. Payload is only the ingestion ID (opaque).
/// </summary>
public sealed class ReplayIngestionJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid ingestionId, Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db     = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ReplayIngestionJob>>();

        var ingestion = await db.LeadIngestions
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == ingestionId && i.TenantId == tenantId);

        if (ingestion is null)
        {
            logger.LogWarning("Replay: ingestion {IngestionId} not found", ingestionId);
            return;
        }

        if (ingestion.RawPayloadJson is null)
        {
            logger.LogWarning("Replay: ingestion {IngestionId} has no payload to replay", ingestionId);
            return;
        }

        try
        {
            // Re-parse the raw payload into a CaptureLeadCommand
            // The payload structure matches the CaptureLeadCommand fields
            var cmd = JsonSerializer.Deserialize<CaptureLeadCommand>(
                ingestion.RawPayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (cmd is null)
            {
                logger.LogWarning("Replay: failed to parse payload for ingestion {IngestionId}", ingestionId);
                return;
            }

            var result = await sender.Send(cmd, CancellationToken.None);

            if (result.IsSuccess)
            {
                logger.LogInformation(
                    "Replay succeeded for ingestion {IngestionId}, lead {LeadId}",
                    ingestionId, result.Value?.LeadId);
            }
            else
            {
                logger.LogWarning(
                    "Replay failed for ingestion {IngestionId}: {Error}",
                    ingestionId, result.Error);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Replay error for ingestion {IngestionId}", ingestionId);
        }
    }
}
