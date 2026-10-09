namespace Sankore.Modules.Integration.Infrastructure.Resilience;

using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// Per-tenant, per-connection OUTBOUND rate limiting (INT-09, criterion 2), configured by
/// <c>ConnectionSettings.RateLimitPerMinute</c>. Zero means unlimited.
///
/// <para>
/// <b>Why the ASP.NET limiter in <c>Program.cs</c> cannot serve this.</b> That one is inbound and
/// partitions by user id (falling back to the caller's IP) and, for the ingest routes, by IP plus
/// public key. It has no tenant partition at all, and could not acquire one usefully: it guards
/// how fast somebody may call US. The limit here guards how fast WE may call a CBS, it is a
/// number the far end's licence fixes rather than a SANKORE policy — which is exactly why it
/// lives on the connection's settings — and the traffic it has to shape (a Hangfire dispatch
/// sweep) never passes through an HTTP request pipeline in the first place.
/// </para>
///
/// <para>
/// Partitioned by <i>tenant and connection</i>, not by connection alone: a connection row is
/// already one tenant's, but the pair is what the criterion names and what makes the key
/// self-evident at a call site — and a shared-infrastructure connection, should one ever exist,
/// would otherwise let one tenant eat another's allowance.
/// </para>
///
/// <para>
/// Never queues. <see cref="TryAcquire(Guid, Guid, int)"/> answers immediately and the caller
/// turns a refusal into <c>INTEGRATION_RATE_LIMITED</c> — a transient failure, so the dispatcher's
/// backoff carries the command to the next window. A limiter that waited would hold a Hangfire
/// worker idle for up to a minute, and the queue has a fixed number of them (see
/// <c>DispatchServiceRegistration</c>): a saturated tenant would starve every other one.
/// </para>
///
/// <para>
/// Singleton. The windows are the state; a scoped limiter would hand every MediatR request a
/// fresh, empty allowance and limit nothing.
/// </para>
/// </summary>
internal sealed class TenantRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, FixedWindowRateLimiter> _windows = new();
    private bool _disposed;

    /// <summary>Whether this call may go out now. False means "not in this window".</summary>
    public bool TryAcquire(Guid tenantId, Guid connectionId, int permitsPerMinute)
    {
        // Unlimited is the default and the common case: most CBS contracts set no ceiling, and
        // creating a limiter with int.MaxValue permits would pay for a window nothing ever
        // closes.
        if (permitsPerMinute <= 0) return true;

        // The configured rate is part of the key, so raising a tenant's ceiling takes effect at
        // once instead of waiting for the old window's limiter to be collected. The old bucket is
        // then simply unused — bounded by the number of distinct rates an operator sets, not by
        // traffic.
        var key = $"{tenantId:N}:{connectionId:N}:{permitsPerMinute}";

        var limiter = _windows.GetOrAdd(key, _ => new FixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = permitsPerMinute,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        using var lease = limiter.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    /// <summary>
    /// The overload a call site actually has to hand: it reads the ceiling off the connection,
    /// so no caller has to remember that zero means unlimited.
    /// </summary>
    public bool TryAcquire(Guid tenantId, IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return TryAcquire(tenantId, connection.Id, connection.Settings?.RateLimitPerMinute ?? 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var limiter in _windows.Values) limiter.Dispose();
        _windows.Clear();
    }
}
