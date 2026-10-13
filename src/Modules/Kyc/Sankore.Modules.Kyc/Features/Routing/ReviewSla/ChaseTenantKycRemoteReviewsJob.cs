namespace Sankore.Modules.Kyc.Features.Routing.ReviewSla;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// One tenant's overdue remote-KYC decisions, chased — with a copy to the branch manager
/// (KRM-B-09).
///
/// <para>
/// <b>What it is for.</b> A remotely collected file routed to a human is a customer who has done
/// their part and is waiting. Nothing else in the platform notices the wait: the back-office list
/// shows the file among the others, and the one notification that went out was an email on the day
/// it arrived. Past the tenant's <c>remote-review-sla-hours</c> this says so again, and says it to
/// the manager as well — which is the whole point of a relance, and also why the manager is copied
/// rather than substituted.
/// </para>
///
/// <para>
/// <b>Idempotence is M08's, not a column here.</b> The chase is keyed on the file and the routing
/// reason, so one overdue decision produces exactly one chase however often the hourly sweep runs
/// — and a customer who sends a complement, which re-routes the file with a new reason, can be
/// chased again. A dedicated "chased at" column would have been a second piece of state to clear
/// on every re-route, and getting that wrong silences the mechanism instead of duplicating it,
/// which is the failure nobody notices.
/// </para>
///
/// <para>
/// A file approved automatically is never here: it carries
/// <see cref="KycValidationRoute.Automatic"/> and nobody is waiting on it.
/// </para>
/// </summary>
public sealed class ChaseTenantKycRemoteReviewsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ChaseTenantKycRemoteReviewsJob> logger)
{
    internal const string TemplateKey = "kyc.remote-review-overdue";

    /// <summary>
    /// A ceiling on one tenant's chase, so a backlog that has built up over a weekend cannot turn
    /// one sweep into a thousand emails. The rest are picked up on the next run — the sweep is
    /// hourly and the oldest files sort first.
    /// </summary>
    private const int MaxPerRun = 200;

    public async Task ExecuteAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<IKycSettings>();
        var administration = scope.ServiceProvider.GetRequiredService<IAdministrationModule>();
        var customers = scope.ServiceProvider.GetRequiredService<ICustomersModule>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var links = scope.ServiceProvider.GetRequiredService<IKycRemoteLinkTokens>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        try
        {
            var slaHours = await settings.GetIntAsync(
                tenantId, KycSettingKeys.RemoteReviewSlaHours, CancellationToken.None);

            // A misconfiguration must not chase every file on every sweep. The range validator
            // refuses zero over HTTP, so this only catches a hand-edited row — and skipping is the
            // quiet direction, unlike a flood of email nobody can stop.
            if (slaHours <= 0)
            {
                logger.LogWarning(
                    "Tenant {TenantId} has remote-review-sla-hours = {Hours}; the chase is skipped",
                    tenantId, slaHours);
                return;
            }

            var cutoff = clock.GetUtcNow().AddHours(-slaHours);

            // Statuses compared explicitly rather than through KycFile.IsOpen, which is Ignore()d
            // in the EF configuration and would not translate.
            var overdue = await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId
                         && f.Channel == KycChannel.RemoteSelfService
                         && f.ValidationRoute == KycValidationRoute.Manual
                         && f.RoutedAt != null
                         && f.RoutedAt <= cutoff
                         && (f.Status == KycFileStatus.Validating
                          || f.Status == KycFileStatus.ComplementRequired))
                .OrderBy(f => f.RoutedAt)
                .Take(MaxPerRun)
                .Select(f => new
                {
                    f.Id,
                    f.CustomerId,
                    f.AgencyId,
                    f.ResponsibleAgentId,
                    f.RoutingReason,
                    f.RoutedAt,
                })
                .ToListAsync();

            if (overdue.Count == 0) return;

            var tenant = await tenants.GetAsync(tenantId);
            var chased = 0;

            foreach (var file in overdue)
            {
                if (file.AgencyId is not { } agencyId || agencyId == Guid.Empty) continue;

                var agent = await administration.ResolveAgencyRecipientAsync(
                    tenantId, agencyId, file.ResponsibleAgentId, CancellationToken.None);

                // The manager, resolved WITHOUT a preference so the cascade lands on them. Null
                // when the agency has none — the chase then goes to the agent alone, which is
                // still better than not chasing.
                var manager = await administration.ResolveAgencyRecipientAsync(
                    tenantId, agencyId, preferredUserId: null, CancellationToken.None);

                var clientName = (await customers.GetClientSummaryAsync(
                    tenantId, file.CustomerId, CancellationToken.None))?.DisplayName;

                var data = new Dictionary<string, object>
                {
                    ["client_name"] = clientName ?? string.Empty,
                    ["routing_reason"] = file.RoutingReason ?? string.Empty,
                    ["waiting_since"] = file.RoutedAt!.Value,
                    ["sla_hours"] = slaHours,
                    ["backoffice_link"] = string.IsNullOrWhiteSpace(tenant?.Fqdn)
                        ? string.Empty
                        : links.BuildBackOfficeLink(tenant.Fqdn, file.Id),
                    ["kyc_file_id"] = file.Id,
                };

                // Both recipients get the same message with the same key SHAPE but their own id in
                // it: M08's idempotency is per message, so a shared key would mean the manager's
                // copy silenced by the agent's.
                foreach (var recipient in new[] { agent, manager }.Distinct())
                {
                    if (recipient is not { } userId) continue;

                    var queued = await administration.NotifyUserAsync(new NotifyUserRequest(
                        TenantId: tenantId,
                        UserId: userId,
                        TemplateKey: TemplateKey,
                        IdempotencyKey: $"{TemplateKey}-{file.Id}-{file.RoutingReason}-{userId}",
                        TemplateData: data), CancellationToken.None);

                    if (queued) chased++;
                }
            }

            if (chased > 0)
            {
                logger.LogInformation(
                    "Tenant {TenantId}: chased {Count} overdue remote-KYC decision(s) "
                    + "out of {Overdue} waiting more than {Hours} h",
                    tenantId, chased, overdue.Count, slaHours);
            }
        }
        catch (Exception ex)
        {
            // Swallowed like every other sweep in this module: a chase is a courtesy, the files
            // are in the back-office backlog either way, and a job that throws is retried by
            // Hangfire on its own schedule.
            logger.LogError(ex,
                "Chasing overdue remote-KYC decisions for tenant {TenantId} threw", tenantId);
        }
    }
}
