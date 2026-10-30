using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Sankore.Modules.Administration.Infrastructure.PaidModules;

/// <summary>
/// The registry's side of « Mode test » (sandbox lot 2): the tenant's test twin, created on the
/// first switch. Idempotent at the registry, so a retried or concurrent switch answers the same twin.
/// </summary>
internal interface ITestTwinRegistry
{
    Task<RegistryResult<Guid>> EnsureTwinAsync(Guid tenantId, CancellationToken ct);
}

internal sealed class HttpTestTwinRegistry(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTestTwinRegistry> logger) : ITestTwinRegistry
{
    private sealed record TwinBody(Guid Id);

    public Task<RegistryResult<Guid>> EnsureTwinAsync(Guid tenantId, CancellationToken ct) =>
        TenantRegistryHttp.SendAsync(httpClientFactory, logger, "a test-twin call",
            c => c.PostAsync($"api/v1/tenants/{tenantId}/test-twin", content: null, ct),
            async r => (await r.Content.ReadFromJsonAsync<TwinBody>(TenantRegistryHttp.Json, ct))!.Id, ct);
}
