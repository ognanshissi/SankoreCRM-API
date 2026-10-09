namespace Sankore.Modules.Integration.Adapters.Temenos;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// One cached access token per CONNECTION, renewed before it expires, minted at most once per
/// burst (INT-12, criterion 2).
///
/// <para>
/// <b>Per connection and never per tenant or per process.</b> An IMF has one core banking
/// connection, but the deployment serves many IMFs and each authenticates with its own client
/// credentials. A token cached per process would be one tenant's token used for another tenant's
/// calls — the same mistake as putting a bearer header on a pooled <c>HttpClient</c>, one layer
/// up. The key is <c>(TenantId, ConnectionId)</c> and not <c>ConnectionId</c> alone: connection
/// ids are ours and unique, but a cache keyed on half an identity is one bug away from a
/// cross-tenant read, and the tenant costs sixteen bytes.
/// </para>
///
/// <para>
/// <b>Single-flight.</b> The dispatcher runs several commands of a tenant concurrently, so a cold
/// cache is hit by N calls at once. Without a guard each would mint its own token: N round trips
/// to the authorisation server, N tokens issued, and on an installation that invalidates the
/// previous token on issue (several do), N-1 calls failing with a 401 that reads like wrong
/// credentials. One waiter mints; the rest wait on the same semaphore and then find the fresh
/// token on the second check.
/// </para>
///
/// <para>
/// <b>Singleton, and therefore holding no scoped service.</b> That is why minting is passed IN as
/// a delegate rather than done here: the credential comes from <c>ISecretsModule</c>, which is
/// scoped, and a singleton that captured it would hold a disposed <c>DbContext</c> for the life of
/// the process. The cache owns storage, expiry and the single-flight guard; <see
/// cref="TemenosAuthenticator"/> owns how a token is obtained.
/// </para>
/// </summary>
internal sealed class TemenosTokenCache(TimeProvider clock, IOptions<TemenosAdapterOptions> options)
{
    private readonly ConcurrentDictionary<TemenosTokenScope, CacheEntry> _tokens = new();
    private readonly ConcurrentDictionary<TemenosTokenScope, SemaphoreSlim> _gates = new();

    /// <summary>
    /// The connection's token, from cache when it is still good for longer than the renewal
    /// margin, and freshly minted otherwise.
    /// </summary>
    /// <param name="forceRenew">
    /// Discards whatever is cached first. Set by the transport after a 401: the installation has
    /// just told us the token it accepted a minute ago is no longer good, and believing our own
    /// cache over that answer is how a connection stays broken until the process restarts.
    /// </param>
    public async Task<IntegrationResult<string>> GetAsync(
        TemenosTokenScope scope,
        Func<CancellationToken, Task<IntegrationResult<TemenosToken>>> mint,
        bool forceRenew,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mint);

        if (forceRenew)
            _tokens.TryRemove(scope, out _);
        else if (TryRead(scope, out var cached))
            return IntegrationResult.Ok(cached);

        var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            // Second check under the gate. This is the whole single-flight guarantee: the waiters
            // that queued behind the one minter find the token it just stored and never call out.
            if (TryRead(scope, out var fresh))
                return IntegrationResult.Ok(fresh);

            var minted = await mint(ct);
            if (minted.IsFailure)
                return IntegrationResultForwarding.Forward<string>(minted);

            _tokens[scope] = new CacheEntry(minted.Value.Value, ExpiryOf(minted.Value));

            return IntegrationResult.Ok(minted.Value.Value);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Forgets a connection's token. Called when the connection's credentials change, so the next
    /// call authenticates with what the administrator just saved rather than with what was good
    /// before it.
    /// </summary>
    public void Invalidate(TemenosTokenScope scope) => _tokens.TryRemove(scope, out _);

    /// <summary>
    /// True when a token is cached AND will still be valid for longer than the renewal margin.
    /// The margin is subtracted from the expiry rather than added to "now" so that a margin wider
    /// than the token's whole lifetime simply means "never cache", which is a usable setting.
    /// </summary>
    private bool TryRead(TemenosTokenScope scope, out string token)
    {
        token = string.Empty;

        if (!_tokens.TryGetValue(scope, out var entry))
            return false;

        if (entry.ExpiresAt - options.Value.TokenRenewalMargin <= clock.GetUtcNow())
        {
            _tokens.TryRemove(scope, out _);
            return false;
        }

        token = entry.Token;
        return true;
    }

    /// <summary>
    /// When the token stops being usable. A token that did not say falls back to the configured
    /// lifetime; see <see cref="TemenosAdapterOptions.FallbackTokenLifetime"/> for why "for ever"
    /// is not an option.
    /// </summary>
    private DateTimeOffset ExpiryOf(TemenosToken token)
        => token.ExpiresAt ?? clock.GetUtcNow() + options.Value.FallbackTokenLifetime;

    private sealed record CacheEntry(string Token, DateTimeOffset ExpiresAt);
}

/// <summary>
/// The cache key. A record struct so it compares by value and allocates nothing on a hit.
/// </summary>
internal readonly record struct TemenosTokenScope(Guid TenantId, Guid ConnectionId);

/// <summary>
/// A minted token and when it stops being usable. <c>null</c> expiry means the authorisation
/// server did not say.
/// </summary>
internal sealed record TemenosToken(string Value, DateTimeOffset? ExpiresAt);

/// <summary>
/// Re-raises a failure under its ORIGINAL family while changing the carried type.
///
/// <para>
/// It exists because <c>IntegrationResult&lt;T&gt;</c>'s factories each hard-code a family, so
/// forwarding a failure from a <c>IntegrationResult&lt;TemenosToken&gt;</c> to a
/// <c>IntegrationResult&lt;string&gt;</c> means naming a family again — and naming the wrong one
/// silently changes whether the dispatcher retries. An authorisation server that is down is
/// <c>Transient</c>; credentials it refused are <c>Technical</c>; collapsing the two would have
/// the platform retry wrong credentials for ever, or alert an administrator for a blip.
/// </para>
/// </summary>
internal static class IntegrationResultForwarding
{
    public static IntegrationResult<T> Forward<T>(IntegrationResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.IsSuccess)
            throw new InvalidOperationException("Only a failure can be forwarded.");

        return source.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional<T>(source.Code!, source.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical<T>(source.Code!, source.Detail),
            _ => IntegrationResult.Transient<T>(source.Code!, source.Detail),
        };
    }

    /// <summary>Same, onto the valueless result.</summary>
    public static IntegrationResult Forward(IntegrationResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.IsSuccess)
            throw new InvalidOperationException("Only a failure can be forwarded.");

        return source.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional(source.Code!, source.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical(source.Code!, source.Detail),
            _ => IntegrationResult.Transient(source.Code!, source.Detail),
        };
    }
}
