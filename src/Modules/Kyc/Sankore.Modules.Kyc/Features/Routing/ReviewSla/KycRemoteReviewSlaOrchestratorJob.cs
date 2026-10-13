namespace Sankore.Modules.Kyc.Features.Routing.ReviewSla;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (KRM-B-09's
/// « relance après KYC_REMOTE_REVIEW_SLA_HOURS sans décision »).
///
/// <para>
/// Same shape and the same reasoning as <c>KycRemoteInvitationExpiryOrchestratorJob</c>: one
/// recurring job per tenant would need registering at every tenant creation and would leave a
/// dead schedule at every deactivation, so a single schedule fans out one
/// <see cref="ChaseTenantKycRemoteReviewsJob"/> per active tenant with nothing but an opaque id.
/// </para>
///
/// <para>
/// <b>Hourly, and not daily like its neighbour.</b> The SLA is expressed in HOURS and defaults to
/// 24: a daily sweep would turn a 24-hour promise into anything between 24 and 48 hours depending
/// on what time of day the file was routed, and an institution that sets it to 4 would get no
/// benefit at all. The sweep is cheap — it reads the remote files of one tenant that are waiting
/// for a decision, which is tens of rows — and it is idempotent, so running it twenty-four times a
/// day costs twenty-three no-ops.
/// </para>
/// </summary>
public sealed class KycRemoteReviewSlaOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<KycRemoteReviewSlaOrchestratorJob> logger)
{
    public const string RecurringJobId = "kyc-remote-review-sla";

    /// <summary>
    /// Every hour, at twenty past. Off the hour deliberately: the platform's other sweeps run on
    /// it, and a chase that competes with the review orchestrator for connections delays the
    /// sweep that actually moves compliance deadlines.
    /// </summary>
    public const string CronExpression = "20 * * * *";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;
            hangfire.Enqueue<ChaseTenantKycRemoteReviewsJob>(job => job.ExecuteAsync(tenantId));
        }

        logger.LogDebug(
            "Queued the remote-KYC review chase for {TenantCount} active tenant(s).",
            tenants.Count);
    }
}
