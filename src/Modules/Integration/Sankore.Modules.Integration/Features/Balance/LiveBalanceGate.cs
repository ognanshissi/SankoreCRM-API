namespace Sankore.Modules.Integration.Features.Balance;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Sankore.Modules.Integration.Infrastructure.Resilience;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Everything INT-15 puts between the ownership check and the core banking call: the 60-second
/// distributed cache, and the question "is this connection's circuit open".
///
/// <para>
/// One type and not two because the facade reads both at the same point of the same method, and a
/// second collaborator on <c>IntegrationModuleFacade</c> would be a second constructor parameter
/// on a file three chantiers are editing at once. They also answer one question between them —
/// <i>must we actually call the CBS?</i> — the cache answering "no, we asked a moment ago" and the
/// breaker answering "no, it is down".
/// </para>
///
/// <para>
/// <b>Nothing here may fail a balance read.</b> Redis is an optimisation: when it is absent, slow
/// or broken the figure is still obtainable from the CBS, so every cache call is wrapped and a
/// failure degrades to a miss. The opposite — a counter that cannot show a balance because a cache
/// node is being restarted — would be a self-inflicted outage.
/// </para>
/// </summary>
internal sealed class LiveBalanceGate(
    IDistributedCache? cache,
    IntegrationResiliencePipelineProvider pipelines,
    ILogger<LiveBalanceGate> logger)
{
    /// <summary>
    /// How long a live figure is served from the cache (INT-15 criterion 1).
    ///
    /// <para>
    /// Spent as <see cref="DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow"/> and
    /// never as a sliding window: a sliding window on a busy account is refreshed by every reader,
    /// so a figure minutes old would keep being served for as long as anyone kept asking — which
    /// is the exact failure <c>IsStale</c> exists to make impossible.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// <c>true</c> when Polly has cut this connection off (INT-09's per-connection breaker).
    ///
    /// <para>
    /// <see cref="CircuitState.HalfOpen"/> is deliberately NOT open: Polly is allowing one trial
    /// call, and a balance read — idempotent, and with somebody waiting for it — is a good
    /// candidate to be that call. <c>null</c> means no call has gone through this connection since
    /// the process started, which is "unknown" and not "broken", so it proceeds too.
    /// </para>
    /// </summary>
    public bool IsCircuitOpen(Guid connectionId)
        => pipelines.GetCircuitState(connectionId) is CircuitState.Open or CircuitState.Isolated;

    /// <summary>
    /// The live figure cached for this account less than <see cref="CacheTtl"/> ago, or
    /// <c>null</c> — a miss, an absent cache and a broken cache being indistinguishable to the
    /// caller on purpose.
    /// </summary>
    public async Task<CbsBalance?> TryReadAsync(
        Guid tenantId, Guid connectionId, ExternalId accountId, CancellationToken ct)
    {
        if (cache is null) return null;

        var key = CacheKey(tenantId, connectionId, accountId.Value);

        try
        {
            var payload = await cache.GetStringAsync(key, ct);
            if (payload is null) return null;

            var entry = JsonSerializer.Deserialize<CachedBalance>(payload, JsonOpts);
            if (entry is null) return null;

            // IsStale is false and is not read back from the entry: only a live success is ever
            // written, so a cache hit is by construction a live figure. See WriteAsync.
            return new CbsBalance(
                AccountId: new ExternalId(entry.AccountId),
                Currency: entry.Currency,
                Balance: entry.Balance,
                AvailableBalance: entry.AvailableBalance,
                AsOf: entry.AsOf,
                IsStale: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes a JsonException on an entry written by an older shape: an unreadable entry
            // is a miss, and the CBS is asked. Logged at debug because a cache miss is not an
            // incident and this runs on every counter screen.
            logger.LogDebug(ex, "Live-balance cache read failed for {CacheKey}; falling through to the CBS", key);
            return null;
        }
    }

    /// <summary>
    /// Remembers a live figure for <see cref="CacheTtl"/>.
    ///
    /// <para>
    /// <b>A stale value is never written.</b> Caching the snapshot fallback would turn one CBS
    /// outage into a frozen figure for a further 60 seconds after the CBS came back — the cache
    /// would be serving yesterday's number while the live system was answering again. The guard is
    /// explicit AND the stored shape carries no staleness flag at all, so there is no way to write
    /// one and no way for a reader to resurrect one.
    /// </para>
    /// </summary>
    public async Task WriteAsync(
        Guid tenantId, Guid connectionId, ExternalId accountId, CbsBalance balance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(balance);

        if (cache is null || balance.IsStale) return;

        var key = CacheKey(tenantId, connectionId, accountId.Value);

        try
        {
            var payload = JsonSerializer.Serialize(
                new CachedBalance(
                    balance.AccountId.Value,
                    balance.Currency,
                    balance.Balance,
                    balance.AvailableBalance,
                    balance.AsOf),
                JsonOpts);

            await cache.SetStringAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller already has its answer; failing to remember it costs one extra CBS call.
            logger.LogDebug(ex, "Live-balance cache write failed for {CacheKey}", key);
        }
    }

    /// <summary>
    /// <c>integration:balance:{tenantId}:{connectionId}:{externalAccountId}</c> — the repo's
    /// <c>module:concern:id</c> shape.
    ///
    /// <para>
    /// Per ACCOUNT and not per customer: two agents looking at two accounts of one customer must
    /// not share an entry, or each would be shown the other's balance.
    /// </para>
    ///
    /// <para>
    /// <b>The tenant is in the key because Redis is one shared instance for every tenant.</b> A key
    /// built from the account reference alone would be a cross-tenant read wherever two IMFs run
    /// the same CBS numbering — and sequential account numbers are the norm, so the collision is
    /// likely rather than theoretical. The connection is in it for the same reason one step down:
    /// the same account number means something different on a different core banking system.
    /// </para>
    ///
    /// <para>
    /// <paramref name="externalAccountId"/> must be the identifier resolved from OUR records, never
    /// the caller's string — see the ownership check in <c>IntegrationModuleFacade</c>. Two
    /// spellings of one account then share one entry, and no caller-supplied text reaches Redis.
    /// </para>
    /// </summary>
    internal static string CacheKey(Guid tenantId, Guid connectionId, string externalAccountId)
        => $"integration:balance:{tenantId}:{connectionId}:{externalAccountId}";

    /// <summary>
    /// What actually sits in Redis. A shape of its own rather than <see cref="CbsBalance"/>: it
    /// holds no staleness flag, which is what makes "a stale value is never cached" a property of
    /// the type and not of a code path somebody has to remember.
    /// </summary>
    private sealed record CachedBalance(
        string AccountId,
        string Currency,
        decimal Balance,
        decimal? AvailableBalance,
        DateTimeOffset AsOf);
}
