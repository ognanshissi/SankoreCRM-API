namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (TEL-B-05, F12.15).
///
/// <para>
/// It fans out one <see cref="CheckTelephonyUserLinksJob"/> per active tenant and does nothing
/// else. The shape is <c>IntegrationSyncOrchestrator</c>'s and the reasons are the same: a
/// recurring job per tenant would have to be registered at every tenant creation and would leave
/// a dead schedule behind at every deactivation, and one Hangfire job per tenant is what makes
/// one tenant's failure cost that tenant its run rather than everybody's.
/// </para>
///
/// <para>
/// <b>Why a nightly check and not a reaction to an event.</b> Nothing tells SANKORE that somebody
/// was removed from the provider's directory — that is a change made in another company's
/// console, and no provider in this family promises a webhook for it. The first visible symptom
/// otherwise is an agent's calls quietly stopping being attributed to them, which reads as a
/// journal bug and is a settings fact.
/// </para>
/// </summary>
public sealed class CheckTelephonyUserLinksOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<CheckTelephonyUserLinksOrchestrator> logger)
{
    /// <summary>
    /// 02:40 daily. After <c>integration-telephony-partitions</c> (02:00) so a check that wrote
    /// anything writes it into a partition that exists, and in the quiet window the other
    /// maintenance jobs of this module already use.
    /// </summary>
    public const string CronExpression = "40 2 * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// it. Kebab-case and nothing else: Hangfire derives distributed lock names from the id, and a
    /// dot or a colon in one breaks them.
    /// </summary>
    public const string RecurringJobId = "integration-telephony-link-check";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression: Hangfire serialises the lambda's arguments,
            // and a captured TenantInfo would put the tenant's name, FQDN and connection string
            // in a job-argument column and on a dashboard page.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant, and owns no connection.
            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<CheckTelephonyUserLinksJob>(job => job.ExecuteAsync(tenantId));
            enqueued++;
        }

        logger.LogInformation(
            "Telephony link check: {Enqueued} tenant run(s) enqueued out of {TenantCount} active "
            + "tenant(s).", enqueued, tenants.Count);
    }
}
