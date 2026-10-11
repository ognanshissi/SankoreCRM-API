namespace Sankore.Modules.Kyc.Features.Reviews;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews.RaiseReview;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Brings a client's file up to date on the ANNIVERSARY OF THEIR CONTRACT, which is what the CIMA
/// LBC/FT text asks for: « les informations sont mises à jour à la date anniversaire du contrat ».
///
/// <para>
/// <b>Why this is not the periodic review already in place.</b> That one runs on the vigilance
/// level — three years for a low-risk file, one for a high-risk one — which is the BCEAO's
/// graduated approach and a different rule with a different clock. A client holding a contract
/// signed in March is to be re-examined every March whatever their risk rating says, and before
/// this job nothing made that happen.
/// </para>
///
/// <para>
/// <b>It raises the review through the same command the manual path uses</b>, so a periodic
/// review, an event-driven one and an anniversary one are announced by exactly one code path —
/// <c>RaiseKycReviewHandler</c> schedules it for today and queues the tenant sweep that tells the
/// agency. Writing a second announcement here is how the two would come to disagree about what an
/// agency is told.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type, like every other job.
public sealed class ContractAnniversaryReviewJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ContractAnniversaryReviewJob> logger)
{
    /// <summary>
    /// The reason stored on the schedule row. In clear and read by an operator, so it names the
    /// cause and carries nothing about the client.
    /// </summary>
    public const string AnniversaryReason = "Mise à jour annuelle — date anniversaire du contrat";

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context. Getting that order wrong is the
        // documented way this solution's jobs fail.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<KycDbContext>();
        var customers = sp.GetRequiredService<ICustomersModule>();
        var sender = sp.GetRequiredService<ISender>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        IReadOnlyList<Guid> clientIds;

        try
        {
            clientIds = await customers.GetClientsWithContractAnniversaryAsync(tenantId, today, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed and logged: tomorrow's run looks at tomorrow's anniversaries, so a failed
            // night loses one day of updates rather than the rule. Throwing would have Hangfire
            // retry against an outage and fill the dashboard.
            logger.LogError(ex,
                "Could not read today's contract anniversaries for tenant {TenantId}; "
                + "no anniversary review was raised",
                tenantId);
            return;
        }

        if (clientIds.Count == 0) return;

        var raised = 0;

        foreach (var clientId in clientIds)
        {
            // The open file of that customer. Status compared explicitly rather than through
            // KycFile.IsOpen, which is Ignore()d in the EF configuration and would not translate.
            var fileId = await db.KycFiles
                .Where(f => f.CustomerId == clientId
                         && f.Status != KycFileStatus.Rejected
                         && f.Status != KycFileStatus.Suspended)
                .Select(f => f.Id)
                .FirstOrDefaultAsync(ct);

            if (fileId == Guid.Empty) continue;

            // Idempotent by the same device the PEP consumer uses, and for the same reason: the
            // scheduler inserts unconditionally, and a job re-run by hand on the same day would
            // otherwise hand the officer a second copy of one task. Scoped to OPEN reviews, so
            // next year's anniversary is owed again.
            var alreadyPending = await db.KycReviewSchedules
                .AnyAsync(
                    r => r.KycFileId == fileId
                      && r.Trigger == KycReviewTrigger.Event
                      && r.Reason == AnniversaryReason
                      && (r.Status == KycReviewStatus.Scheduled || r.Status == KycReviewStatus.Due),
                    ct);

            if (alreadyPending) continue;

            try
            {
                // Guid.Empty as the actor: SYSTEM raised it, and attributing it to a person would
                // put a name on the one review nobody decided to ask for.
                var result = await sender.Send(
                    new RaiseKycReviewCommand(fileId, AnniversaryReason, Guid.Empty), ct);

                if (result.IsSuccess) raised++;
                else
                {
                    logger.LogWarning(
                        "No anniversary review raised on KYC file {KycFileId}: {Error}",
                        fileId, result.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One client's failure must not stop the others': the rest of the tenant's
                // anniversaries are still owed today.
                logger.LogError(ex,
                    "Anniversary review threw on KYC file {KycFileId} (tenant {TenantId})",
                    fileId, tenantId);
            }
        }

        if (raised > 0)
        {
            logger.LogInformation(
                "Raised {Count} contract-anniversary KYC review(s) for tenant {TenantId} on {Day}",
                raised, tenantId, today);
        }
    }
}
