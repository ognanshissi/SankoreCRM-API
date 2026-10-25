namespace Sankore.Api.Features.Retention;

using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

/// <summary>
/// Empties the TEST institutions a platform administrator asked to wipe (data-purge lot 4).
///
/// <para>
/// <b>Pulled, not pushed.</b> The registry (Sankore.Admin) records the request; this job reads the
/// pending ones with the service key — the direction everything already goes between the two —
/// takes each one with a conditional start (two API instances cannot both take it), wipes, and
/// reports back what it removed or why it failed.
/// </para>
///
/// <para>
/// <b>Checked again here.</b> The registry refused a non-test institution at the request; this
/// job reads the institution FRESH from the registry (not the cached tenant store) and refuses it
/// again if it is not a test one any more — a flag lifted between the request and the run.
/// </para>
///
/// <para>
/// Each module empties its own schema (<see cref="ITenantDataWipe"/>), in one transaction per
/// module; files go after their rows. A module that fails fails the wipe, reported with its name:
/// the others already wiped stay wiped — it is a test institution, and running the wipe again
/// finishes the job.
/// </para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
public sealed class TenantDataWipeJob(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<TenantDataWipeJob> logger)
{
    public const string RecurringJobId = "tenant-data-wipes";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PendingWipe(Guid Id, Guid TenantId);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var registry = httpClientFactory.CreateClient(TenantStoreServiceCollectionExtensions.HttpClientName);
        var pending = await registry.GetFromJsonAsync<List<PendingWipe>>("api/v1/data-wipes/pending", Json, ct) ?? [];

        foreach (var wipe in pending)
        {
            var tenant = await FreshTenantAsync(registry, wipe.TenantId, ct);
            if (tenant is not { IsTest: true })
            {
                await registry.PostAsJsonAsync($"api/v1/data-wipes/{wipe.Id}/fail",
                    new { Error = "TENANT_NOT_TEST: the institution is not (or no longer) marked as a test one." }, Json, ct);
                logger.LogError("Data wipe {WipeId} refused: tenant {TenantId} is not a test institution.", wipe.Id, wipe.TenantId);
                continue;
            }

            var started = await registry.PostAsync($"api/v1/data-wipes/{wipe.Id}/start", content: null, ct);
            if (!started.IsSuccessStatusCode) continue; // taken by another instance

            logger.LogWarning("Data wipe {WipeId}: emptying TEST tenant {TenantId} ({Name}).", wipe.Id, tenant.Id, tenant.Name);
            var summary = new Dictionary<string, int>();
            var current = "";
            try
            {
                // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
                using var bg = BackgroundJobContext.SetScope(tenant.Id, Guid.Empty, "SYSTEM");
                using var scope = scopeFactory.CreateScope();
                foreach (var module in scope.ServiceProvider.GetServices<ITenantDataWipe>())
                {
                    current = module.Module;
                    summary[module.Module] = await module.WipeAsync(tenant.Id, ct);
                }

                await registry.PostAsJsonAsync($"api/v1/data-wipes/{wipe.Id}/complete",
                    new { Summary = JsonSerializer.Serialize(summary, Json) }, Json, ct);
                logger.LogWarning("Data wipe {WipeId} done for tenant {TenantId}: {Summary}.", wipe.Id, tenant.Id, summary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Data wipe {WipeId} failed in module {Module} for tenant {TenantId}.", wipe.Id, current, tenant.Id);
                await registry.PostAsJsonAsync($"api/v1/data-wipes/{wipe.Id}/fail",
                    new { Error = $"{current}: {ex.GetType().Name} — {ex.Message}" }, Json, ct);
            }
        }
    }

    private async Task<TenantInfo?> FreshTenantAsync(HttpClient registry, Guid tenantId, CancellationToken ct)
    {
        try
        {
            return await registry.GetFromJsonAsync<TenantInfo>($"api/v1/tenants/{tenantId}", Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Data wipe: tenant {TenantId} could not be read from the registry.", tenantId);
            return null;
        }
    }
}
