namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Hangfire job — one tenant's user links, checked against the provider's directory (TEL-B-05).
/// Enqueued by <see cref="CheckTelephonyUserLinksOrchestrator"/>.
/// </summary>
public sealed class CheckTelephonyUserLinksJob(IServiceScopeFactory scopeFactory)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first captures the HTTP
        // implementations and finds no request. SYSTEM is the actor — nobody asked for this, a
        // schedule elapsed.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<CheckTelephonyUserLinksJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<TelephonyAccess>(),
            sp.GetRequiredKeyedService<IEventPublisher>(nameof(IntegrationDbContext)),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Telephony link check for tenant {TenantId}: {Checked} link(s) checked, {Broke} newly "
            + "broken, {Restored} restored. Skipped: {Skipped}.",
            tenantId, report.Checked, report.Broke, report.Restored, report.SkippedBecause ?? "no");
    }

    /// <summary>
    /// The check itself, with its collaborators passed in — so the one property that matters can
    /// be pinned by a test without a DI container or a Hangfire storage.
    ///
    /// <para>
    /// <b>An unreachable provider breaks nothing.</b> The directory read failing is an outage, and
    /// treating "I could not ask" as "the account is gone" would break every link of a tenant in
    /// one run, publish an event per agent, and leave an administrator repairing by hand what was
    /// never wrong. The run is skipped, loudly. Same reasoning M02 settled on for biometry:
    /// rejecting an honest subject over our own downtime is worse than waiting.
    /// </para>
    ///
    /// <para>
    /// <b>An EMPTY directory is also an outage, not an institution that fired everybody.</b> A
    /// provider answering a successful, empty list while its directory endpoint is half-broken is
    /// indistinguishable from a tenant with no accounts — except that a tenant with no accounts
    /// has no links either, so there is nothing to check and nothing is lost by skipping. The one
    /// reading that cannot cause harm is the one taken.
    /// </para>
    ///
    /// <para>
    /// The event is published through the OUTBOX, inside this transaction, so "the link is broken"
    /// and "somebody was told" commit together. Published on the TRANSITION only: a link already
    /// broken yesterday produces no second message, or an administrator is mailed every morning
    /// about an account that is not coming back.
    /// </para>
    /// </summary>
    internal static async Task<LinkCheckReport> RunAsync(
        IntegrationDbContext db,
        TelephonyAccess access,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var use = await access.ResolvePortAsync(
            tenantId, IntegrationCapability.UserDirectory, ct);

        if (use.IsFailure)
        {
            // Telephony switched off, or a provider that publishes no directory. Neither is an
            // incident: most tenants of this platform have no telephony at all, and a job that
            // logged a warning per tenant per night would train everybody to ignore it.
            logger.LogDebug(
                "Telephony link check skipped for tenant {TenantId}: {Code}.", tenantId, use.Code);

            return LinkCheckReport.Skipped(use.Code!);
        }

        var connectionId = use.Value.Connection.Id;

        // Only the links this check can act on. A Disabled link is a human decision and is never
        // revisited here; a Broken one is, because the account may be back.
        var links = await db.TelUserLinks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(l => l.TenantId == tenantId
                     && l.ConnectionId == connectionId
                     && l.Status != TelUserLinkStatus.Disabled)
            .ToListAsync(ct);

        if (links.Count == 0)
            return LinkCheckReport.Skipped("NO_LINKS");

        var users = await use.Value.Port.ListUsersAsync(ct);

        if (users.IsFailure)
        {
            logger.LogWarning(
                "Telephony link check skipped for tenant {TenantId}: the provider's directory is "
                + "unavailable ({Family}/{Code}). No link is marked broken on an outage.",
                tenantId, users.Family, users.Code);

            return LinkCheckReport.Skipped(users.Code!);
        }

        if (users.Value.Count == 0)
        {
            logger.LogWarning(
                "Telephony link check skipped for tenant {TenantId}: the provider answered an "
                + "EMPTY directory while {LinkCount} link(s) exist. Treated as an outage.",
                tenantId, links.Count);

            return LinkCheckReport.Skipped("EMPTY_DIRECTORY");
        }

        var active = users.Value
            .Where(u => u.IsActive)
            .Select(u => u.Id.Value)
            .ToHashSet(StringComparer.Ordinal);

        var known = users.Value
            .Select(u => u.Id.Value)
            .ToHashSet(StringComparer.Ordinal);

        var now = clock.GetUtcNow();
        var broke = 0;
        var restored = 0;

        foreach (var link in links)
        {
            if (active.Contains(link.ExternalUserId))
            {
                if (link.Restore()) restored++;
                continue;
            }

            // Two reasons and they are told apart, because they are different repairs: an account
            // removed from the directory means the person left the provider, while one listed as
            // disabled is usually a licence somebody can re-enable.
            var reason = known.Contains(link.ExternalUserId)
                ? "The provider lists this account as disabled."
                : "The account is no longer in the provider's directory.";

            // Already broken yesterday: the reason is refreshed (a disabled account that was then
            // removed is worth saying) and nothing is published.
            var wasBroken = link.Status == TelUserLinkStatus.Broken;

            link.MarkBroken(reason);

            if (wasBroken) continue;

            broke++;

            await publisher.PublishAsync(
                new TelephonyUserLinkBrokenEvent(
                    tenantId,
                    connectionId,
                    link.Id,
                    link.CrmUserId,
                    link.ExternalUserId,
                    reason,
                    now),
                ct);
        }

        if (broke > 0 || restored > 0)
            await db.SaveChangesAsync(ct);

        return new LinkCheckReport(links.Count, broke, restored, null);
    }

    /// <summary>
    /// What one run did. <paramref name="Skipped"/> carries WHY nothing was checked, because
    /// "0 links checked" on its own cannot be told from "the provider was down".
    /// </summary>
    internal sealed record LinkCheckReport(
        int Checked, int Broke, int Restored, string? SkippedBecause)
    {
        internal static LinkCheckReport Skipped(string reason) => new(0, 0, 0, reason);
    }
}
