namespace Sankore.Modules.Administration.Infrastructure.PaidModules;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>The registry's answer for one module of one tenant (Sankore.Admin's TenantModuleResponse).</summary>
internal sealed record RegistryTenantModule(
    string Code,
    string Label,
    string Description,
    decimal MonthlyPrice,
    string Currency,
    bool IsAvailable,
    string State,
    RegistryModuleSubscription? Current,
    IReadOnlyList<RegistryModuleSubscription> History);

internal sealed record RegistryModuleSubscription(
    Guid Id,
    string ModuleCode,
    string Status,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RequestedAt,
    string? RequestedByEmail,
    decimal? AcceptedMonthlyPrice,
    string? AcceptedCurrency,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivatedByEmail);

internal sealed record RegistryTenantModules(Guid TenantId, IReadOnlyList<RegistryTenantModule> Modules);

/// <summary>
/// What a registry call answered: a value, or an error code — the registry's own
/// (<c>MODULE_PRICE_CHANGED</c>…), relayed as is, or <see cref="Unavailable"/> when it could not
/// be reached or failed. <see cref="Detail"/> carries the registry's explanation when it gave one.
/// </summary>
internal sealed record RegistryResult<T>(T? Value, string? Error, string? Detail = null)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Writes and reads a tenant's paid modules in the registry (Sankore.Admin). The tenant id and the
/// acting user are always the caller's own — derived from the token by the handlers, never taken
/// from a request body — because the service key this client carries can act on any tenant.
/// </summary>
internal interface ITenantModuleRegistry
{
    Task<RegistryResult<RegistryTenantModules>> ListAsync(Guid tenantId, CancellationToken ct);

    Task<RegistryResult<Guid>> RequestAsync(Guid tenantId, string module, Guid userId, string email,
        decimal acceptedMonthlyPrice, string acceptedCurrency, CancellationToken ct);

    Task<RegistryResult<bool>> CancelAsync(Guid tenantId, Guid subscriptionId, Guid userId, CancellationToken ct);

    Task<RegistryResult<bool>> DeactivateAsync(Guid tenantId, string module, Guid userId, string email,
        CancellationToken ct);
}

internal sealed class HttpTenantModuleRegistry(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTenantModuleRegistry> logger) : ITenantModuleRegistry
{
    public const string Unavailable = "TENANT_REGISTRY_UNAVAILABLE";

    private static JsonSerializerOptions Json => TenantRegistryHttp.Json;

    private sealed record CreatedBody(Guid Id);

    public Task<RegistryResult<RegistryTenantModules>> ListAsync(Guid tenantId, CancellationToken ct) =>
        SendAsync(c => c.GetAsync($"api/v1/tenants/{tenantId}/modules/", ct),
            async r => (await r.Content.ReadFromJsonAsync<RegistryTenantModules>(Json, ct))!, ct);

    public Task<RegistryResult<Guid>> RequestAsync(Guid tenantId, string module, Guid userId, string email,
        decimal acceptedMonthlyPrice, string acceptedCurrency, CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"api/v1/tenants/{tenantId}/modules/{Uri.EscapeDataString(module)}/requests",
                new { userId, email, acceptedMonthlyPrice, acceptedCurrency }, Json, ct),
            async r => (await r.Content.ReadFromJsonAsync<CreatedBody>(Json, ct))!.Id, ct);

    public Task<RegistryResult<bool>> CancelAsync(Guid tenantId, Guid subscriptionId, Guid userId,
        CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"api/v1/tenants/{tenantId}/modules/requests/{subscriptionId}/cancel",
                new { userId }, Json, ct),
            _ => Task.FromResult(true), ct);

    public Task<RegistryResult<bool>> DeactivateAsync(Guid tenantId, string module, Guid userId, string email,
        CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"api/v1/tenants/{tenantId}/modules/{Uri.EscapeDataString(module)}/deactivate",
                new { userId, email }, Json, ct),
            _ => Task.FromResult(true), ct);

    private Task<RegistryResult<T>> SendAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<T>> read,
        CancellationToken ct) =>
        TenantRegistryHttp.SendAsync(httpClientFactory, logger, "a module call", send, read, ct);
}

/// <summary>
/// One call to the registry with the service key, and how its answer is read — shared by every
/// area that relays to it (paid modules, support), so they agree on what is the tenant's mistake
/// and what is our outage.
/// </summary>
internal static class TenantRegistryHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ErrorBody(string? Error, string? Detail);

    public static async Task<RegistryResult<T>> SendAsync<T>(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string what,
        Func<HttpClient, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<T>> read,
        CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(TenantStoreServiceCollectionExtensions.HttpClientName);
            using var response = await send(client);

            if (response.IsSuccessStatusCode)
                return new RegistryResult<T>(await read(response), null);

            // A 4xx carries the registry's own code, which the tenant's front-end understands.
            // Anything else — a 5xx, a 401 because the service key is wrong — is our outage,
            // not the tenant's mistake, and is reported as such.
            if ((int)response.StatusCode is >= 400 and < 500 and not 401)
            {
                var body = await TryReadErrorAsync(response, ct);
                if (body?.Error is { Length: > 0 } code)
                    return new RegistryResult<T>(default, code, body.Detail);
            }

            logger.LogError("Tenant registry answered {Status} to {What}", (int)response.StatusCode, what);
            return new RegistryResult<T>(default, HttpTenantModuleRegistry.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogError(ex, "Tenant registry unreachable for {What}", what);
            return new RegistryResult<T>(default, HttpTenantModuleRegistry.Unavailable);
        }
    }

    private static async Task<ErrorBody?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
