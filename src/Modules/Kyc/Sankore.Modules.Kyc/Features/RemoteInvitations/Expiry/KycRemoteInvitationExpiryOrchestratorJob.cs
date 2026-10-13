namespace Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (KRM-B-04).
///
/// <para>
/// Same shape as <c>KycReviewOrchestratorJob</c> next door, and for the same reason: one recurring
/// job per tenant would have to be registered at every tenant creation and would leave a dead
/// schedule behind at every deactivation. A single daily schedule walks the active tenants and
/// fans out one <see cref="ExpireTenantKycRemoteInvitationsJob"/> each, carrying nothing but an
/// opaque tenant id.
/// </para>
///
/// <para>
/// <b>What the sweep is for, given that an expired link already stops working.</b>
/// <c>KycRemoteInvitation.IsUsableAt</c> compares the clock, so a link is dead the moment its
/// window closes whether or not a job has run — nothing here is a security control. The sweep
/// exists to make the dead link VISIBLE: it moves the row to <c>Expired</c> so the agent's panel
/// says "expiré" instead of "envoyé", and it tells the responsible agent that a customer they
/// converted never finished.
/// </para>
/// </summary>
public sealed class KycRemoteInvitationExpiryOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<KycRemoteInvitationExpiryOrchestratorJob> logger)
{
    public const string RecurringJobId = "kyc-remote-invitation-expiry";

    /// <summary>
    /// 02:30 daily. After the KYC review sweep at 01:00 and before M01's 03:00 scoring run, so a
    /// day's expiries are settled before anything reads them — and at an hour when no branch is
    /// open, because the notifications it produces should greet an agent at the start of a day
    /// rather than interrupt one.
    /// </summary>
    public const string CronExpression = "30 2 * * *";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;
            hangfire.Enqueue<ExpireTenantKycRemoteInvitationsJob>(job => job.ExecuteAsync(tenantId));
        }

        logger.LogInformation(
            "Queued the remote-collection link expiry sweep for {TenantCount} active tenant(s).",
            tenants.Count);
    }
}
