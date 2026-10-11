namespace Sankore.Modules.Kyc.Features.Reviews.Consumers;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews.RaiseReview;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Opens a KYC review when M01 reports that a client's PEP exposure has RISEN (BNF-B-13).
///
/// <para>
/// M01 publishes <see cref="ClientPepExposureRaisedEvent"/> only on a rise, never on a fall:
/// asking a compliance officer to re-review a file because a mandate expired would be noise. So
/// this consumer does not second-guess the event — if it arrived, a review is owed.
/// </para>
///
/// <para>
/// <b>Why M02 and not M08.</b> The plan said "wire an M08 consumer", and the code says otherwise:
/// <c>RaiseKycReviewCommand</c> is <c>internal</c> to this module, and its handler already
/// enqueues <c>ProcessTenantKycReviewsJob</c> — the same idempotent sweep that marks the review
/// due, moves the file to <c>UnderReview</c> and notifies the agency. Announcing it from M08 as
/// well would give one event two announcements, which is exactly what the sweep exists to avoid.
/// </para>
///
/// <para>
/// <b>Idempotence is business-level, and it has to be.</b> <c>KycReviewScheduler.ScheduleEvent</c>
/// does an unconditional insert, and this module has no inbox table (<c>IInboxGuard</c> belongs to
/// M01), so a redelivered message would schedule a second review. The guard below is a lookup for
/// an OPEN event-driven review carrying <see cref="PepReason"/> on the same file. Scoping it to
/// open reviews is deliberate: once the officer has handled the first one, a genuine later rise
/// must be able to raise its own.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class ClientPepExposureRaisedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientPepExposureRaisedConsumer> logger)
    : IConsumer<ClientPepExposureRaisedEvent>
{
    /// <summary>
    /// Stored in clear and read by an operator, so it carries no name, no function, no country —
    /// nothing of the classification itself. The detail stays behind the PEP read permission on
    /// the client record, which is where an officer with the right to see it will look.
    /// </summary>
    public const string PepReason = "Exposition PPE relevée sur la fiche client";

    public async Task Consume(ConsumeContext<ClientPepExposureRaisedEvent> context)
    {
        var evt = context.Message;

        // SYSTEM placeholder tenant: there is no tenant to open a review for.
        if (evt.TenantId == Guid.Empty) return;

        // The ambient scope must be established BEFORE the DI scope: ITenantContext and
        // ICurrentUser are built from it, and a consumer has no HTTP context to fall back on.
        // evt.DeclaredBy, not a SYSTEM placeholder — the review is owed because a named agent
        // declared an exposure, and the audit row must say so.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.DeclaredBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            // The tenant query filter applies — the ambient scope above is what binds it. Status
            // is compared explicitly rather than through KycFile.IsOpen, which is Ignore()d in
            // the EF configuration and would not translate.
            var fileId = await db.KycFiles
                .Where(f => f.CustomerId == evt.ClientId
                         && f.Status != KycFileStatus.Rejected
                         && f.Status != KycFileStatus.Suspended)
                .Select(f => f.Id)
                .FirstOrDefaultAsync(context.CancellationToken);

            if (fileId == Guid.Empty)
            {
                // Ordinary rather than exceptional: a client may carry an exposure before anyone
                // has opened a file, or the only file may have been rejected. Swallowed for the
                // reason ClientCreatedKycConsumer documents — a condition that will not change on
                // redelivery must not loop through the broker.
                logger.LogWarning(
                    "PEP exposure raised on client {ClientId} (tenant {TenantId}) but it has no "
                    + "open KYC file; no review scheduled",
                    evt.ClientId, evt.TenantId);
                return;
            }

            var alreadyPending = await db.KycReviewSchedules
                .AnyAsync(
                    r => r.KycFileId == fileId
                      && r.Trigger == KycReviewTrigger.Event
                      && r.Reason == PepReason
                      && (r.Status == KycReviewStatus.Scheduled || r.Status == KycReviewStatus.Due),
                    context.CancellationToken);

            if (alreadyPending)
            {
                logger.LogDebug(
                    "KYC file {KycFileId} already has an open PEP review; nothing scheduled",
                    fileId);
                return;
            }

            var result = await sender.Send(
                new RaiseKycReviewCommand(fileId, PepReason, evt.DeclaredBy),
                context.CancellationToken);

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "No PEP review scheduled on KYC file {KycFileId} (tenant {TenantId}): {Error}",
                    fileId, evt.TenantId, result.Error);
                return;
            }

            logger.LogInformation(
                "PEP review {ReviewId} scheduled on KYC file {KycFileId} (client {ClientId}), due {DueDate}",
                result.Value.ReviewId, fileId, evt.ClientId, result.Value.DueDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation is the host shutting down and must keep propagating so the message
            // is redelivered rather than lost. Everything else is logged and dropped: the
            // exposure itself is recorded on the client record, and a review that failed to open
            // is visible from the compliance backlog.
            logger.LogError(ex,
                "Could not schedule the PEP review for client {ClientId} (tenant {TenantId})",
                evt.ClientId, evt.TenantId);
        }
    }
}
