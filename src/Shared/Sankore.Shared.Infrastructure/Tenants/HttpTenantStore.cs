using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Verifies tenant existence by calling the external tenant registry.
/// Contract: GET {BaseUrl}/tenants/{tenantId} → 200 (exists) | 404 (unknown).
/// Any non-2xx/404 response is treated as "unknown" and logged.
/// </summary>
internal sealed class HttpTenantStore(
    IHttpClientFactory httpClientFactory,
    IOptions<TenantStoreOptions> options,
    ILogger<HttpTenantStore> logger) : ITenantStore
{
    private const string ClientName = "TenantStore";

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            var response = await client.GetAsync($"tenants/{tenantId}", ct);

            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;

            logger.LogWarning(
                "Tenant store returned unexpected status {Status} for tenant {TenantId}",
                (int)response.StatusCode, tenantId);

            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach tenant store for tenant {TenantId}", tenantId);
            return false;
        }
    }
}
