namespace Sankore.Modules.Integration.Features.Messaging;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Finds the messaging connection a question is about (WA-BE-02) — by the provider's number id
/// for an inbound webhook, by agency for an outbound message — and the port that serves it.
///
/// <para>
/// <b>One number per institution or one per agency is still an open question</b> of the
/// specification, so both are served: a connection may name an agency, and an agency's lead is
/// written to from that agency's number when one exists, from the institution-wide number (no
/// agency) otherwise. An institution that runs a single number simply leaves the agency empty.
/// </para>
/// </summary>
internal sealed class MessagingChannels(
    IntegrationDbContext db,
    IntegrationAdapterResolver resolver,
    IDistributedCache? cache,
    ILogger<MessagingChannels> logger)
{
    /// <summary>Redis key of the number → connection directory, as the specification names it.</summary>
    internal static string CacheKey(string phoneNumberId) => $"wa:pnid:{phoneNumberId}";

    /// <summary>How long a directory entry lives — the specification's 24 hours.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// The ACTIVE messaging connection answering to this number id, whatever its tenant, or null.
    ///
    /// <para>
    /// <b>The cache is self-healing rather than invalidated.</b> An entry names a connection id,
    /// and the row is re-read by its key and checked — still active, still this number — before
    /// it is trusted; a stale entry is dropped and the directory asked again. So deactivating a
    /// number or moving it to another row takes effect on the next delivery even if nothing
    /// cleared Redis, and a deployment without Redis simply asks the database every time. What the
    /// cache saves is the global lookup by address; the row read by primary key remains, because
    /// the secret that verifies the delivery has to be read anyway.
    /// </para>
    /// </summary>
    public async Task<IntegrationConnection?> FindByAddressAsync(string phoneNumberId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId)) return null;

        var address = phoneNumberId.Trim();

        if (await ReadCachedIdAsync(address, ct) is { } cachedId)
        {
            var cached = await db.Connections
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == cachedId, ct);

            if (cached is { IsActive: true, Family: IntegrationFamily.Messaging } && cached.ChannelAddress == address)
                return cached;

            await ForgetAsync(address, ct);
        }

        // No tenant filter, and that is the point: the caller is an anonymous webhook. The global
        // unique index over active rows guarantees there is at most one answer.
        var connection = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.ChannelAddress == address
                     && c.IsActive
                     && c.Family == IntegrationFamily.Messaging)
            .FirstOrDefaultAsync(ct);

        if (connection is not null)
            await RememberAsync(address, connection.Id, ct);

        return connection;
    }

    /// <summary>The tenant's messaging connections, oldest first.</summary>
    public Task<List<IntegrationConnection>> ListAsync(Guid tenantId, bool activeOnly, CancellationToken ct)
        => db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Family == IntegrationFamily.Messaging
                     && (!activeOnly || c.IsActive))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    /// <summary>One of the tenant's messaging connections, active or not.</summary>
    public Task<IntegrationConnection?> GetAsync(Guid tenantId, Guid connectionId, CancellationToken ct)
        => db.Connections
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId && c.Id == connectionId && c.Family == IntegrationFamily.Messaging,
                ct);

    /// <summary>
    /// The number that writes to a lead of <paramref name="agencyId"/>: the agency's own when it
    /// has one, the institution-wide one (no agency) otherwise, null when neither is active.
    ///
    /// <para>
    /// Never ANOTHER agency's number: a branch's customer receiving a message from a different
    /// branch's number would answer to the wrong team, and nothing would route the reply back.
    /// </para>
    /// </summary>
    public async Task<IntegrationConnection?> ResolveForAgencyAsync(
        Guid tenantId, Guid? agencyId, CancellationToken ct)
    {
        var active = await ListAsync(tenantId, activeOnly: true, ct);

        if (agencyId is { } agency
            && active.FirstOrDefault(c => AgencyOf(c) == agency) is { } own)
        {
            return own;
        }

        return active.FirstOrDefault(c => AgencyOf(c) is null);
    }

    /// <summary>The agency a connection serves, read from its settings. Null is institution-wide.</summary>
    public static Guid? AgencyOf(IntegrationConnection connection) => connection.Settings switch
    {
        Dialog360Settings d => d.AgencyId,
        _ => null,
    };

    /// <summary>The number as customers see it, for the screens. Never a secret.</summary>
    public static string? DisplayNumberOf(IntegrationConnection connection) => connection.Settings switch
    {
        Dialog360Settings d => d.DisplayPhoneNumber,
        FakeSettings f when f.PhoneNumberId is not null => $"Numéro simulé ({f.PhoneNumberId})",
        _ => null,
    };

    /// <summary>
    /// The conversational port of a connection, provided its adapter claims
    /// <paramref name="capability"/>.
    /// </summary>
    public IntegrationResult<IConversationalChannelPort> PortFor(
        IntegrationConnection connection, IntegrationCapability capability)
    {
        var adapter = resolver.ResolveFor(connection);

        return adapter.IsFailure
            ? IntegrationResult.Technical<IConversationalChannelPort>(adapter.Code!, adapter.Detail)
            : resolver.ResolvePort<IConversationalChannelPort>(adapter.Value, capability);
    }

    private async Task<Guid?> ReadCachedIdAsync(string address, CancellationToken ct)
    {
        if (cache is null) return null;

        try
        {
            var value = await cache.GetStringAsync(CacheKey(address), ct);
            return Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis down is a slower webhook, never a refused one.
            logger.LogDebug("Messaging directory cache unavailable ({Reason}); reading the database.", ex.GetType().Name);
            return null;
        }
    }

    private async Task RememberAsync(string address, Guid connectionId, CancellationToken ct)
    {
        if (cache is null) return;

        try
        {
            await cache.SetStringAsync(
                CacheKey(address),
                connectionId.ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Messaging directory cache unavailable ({Reason}); not cached.", ex.GetType().Name);
        }
    }

    /// <summary>Drops one directory entry. Also called when a number is edited or switched off.</summary>
    public async Task ForgetAsync(string? address, CancellationToken ct)
    {
        if (cache is null || string.IsNullOrWhiteSpace(address)) return;

        try
        {
            await cache.RemoveAsync(CacheKey(address.Trim()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Messaging directory cache unavailable ({Reason}); entry left to expire.", ex.GetType().Name);
        }
    }
}
