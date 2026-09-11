using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Fetches the full tenant record from the external tenant registry (Sankore.Admin).
/// Contract: GET {BaseUrl}/api/v1/tenants/{tenantId} → 200 TenantInfo | 404 unknown.
/// Any non-2xx/404 response is treated as unknown and logged.
/// </summary>
internal sealed class HttpTenantStore(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTenantStore> logger) : ITenantStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ClientName = "TenantStore";

    public Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
        => FetchAsync(
            c => c.GetAsync($"api/v1/tenants/{tenantId}", ct),
            $"tenant {tenantId}",
            ct);

    public Task<TenantInfo?> GetByFqdnAsync(string fqdn, CancellationToken ct = default)
        => FetchAsync(
            c => c.GetAsync($"api/v1/tenants/by-fqdn/{fqdn}", ct),
            $"fqdn {fqdn}",
            ct);

    private async Task<TenantInfo?> FetchAsync(
        Func<HttpClient, Task<HttpResponseMessage>> request,
        string context,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            var response = await request(client);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Tenant store returned unexpected status {Status} for {Context}",
                    (int)response.StatusCode, context);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TenantInfo>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach tenant store for {Context}", context);
            return null;
        }
    }
}
