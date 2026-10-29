namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using System.Net;
using Sankore.Shared.Kernel;

internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>A partner's endpoint: records what it receives and answers the scripted statuses in turn (the last one repeats).</summary>
internal sealed class RecordingPartner(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    public List<(Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            await request.Content!.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(statuses[Math.Min(Requests.Count - 1, statuses.Length - 1)]);
    }
}

internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The vault's contract, held in a dictionary: values, masks and expiry dates.</summary>
internal sealed class InMemorySecrets : ISecretsModule
{
    private readonly Dictionary<SecretKey, (string Value, DateTimeOffset? ExpiresAt)> _entries = [];

    public Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        _entries[key] = (value, expiresAt);
        return Task.CompletedTask;
    }

    public Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default)
        => Task.FromResult(_entries.TryGetValue(key, out var e)
            ? new SecretHint(key.Name, e.Value[..4] + "****" + e.Value[^4..], e.ExpiresAt)
            : null);

    public Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default)
        => Task.FromResult(_entries.TryGetValue(key, out var e) ? e.Value : null);

    public Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default)
    {
        foreach (var key in _entries.Keys.Where(k => k.TenantId == tenantId && k.Scope == scope && k.EntityId == entityId).ToList())
            _entries.Remove(key);
        return Task.CompletedTask;
    }
}
