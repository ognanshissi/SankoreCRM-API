namespace Sankore.Modules.Integration.Features.Insurance.Claims;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-09, second criterion: the insurer acknowledged the declaration and gave its dossier
/// reference, so the claim can be followed up and the customer can be given a number.
///
/// <para>
/// <b>There is no symmetric "declaration refused" consumer, and that is deliberate.</b> A rejected
/// <c>DeclareClaim</c> command means the TRANSMISSION failed — a timeout, a payload the insurer's
/// API would not take, a connection switched off — not that the insurer refused the claim. INT-06
/// and INT-07 already own the retry and the rejection queue for that, and writing
/// <c>ClaimStatus.Refused</c> here would tell an agent, and then a customer, that their claim was
/// turned down because of our own outage. The dossier stays <c>Declared</c> until a retry lands,
/// which is what it is.
/// </para>
/// </summary>
// Public: MassTransit instantiates it.
public sealed class ClaimDeclarationAcknowledgedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClaimDeclarationAcknowledgedConsumer> logger)
    : IConsumer<IntegrationCommandSucceededEvent>
{
    internal const string ConsumerKey = nameof(ClaimDeclarationAcknowledgedConsumer);

    public async Task Consume(ConsumeContext<IntegrationCommandSucceededEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // Filtered on the COMMAND TYPE and not on the entity type: a Claim entity type is also
        // what a later document-transmission command will carry, and two consumers both answering
        // to it would each half-handle the other's message.
        if (evt.CommandType != nameof(CommandType.DeclareClaim)) return;

        // Nothing to record. The reference IS what this consumer exists to store, and
        // RecordInsurerReference refuses a blank one — rightly: a dossier with an empty insurer
        // reference is irreconcilable, exactly as an Active holding with no ExternalRef is.
        if (string.IsNullOrWhiteSpace(evt.ExternalRef))
        {
            logger.LogWarning(
                "Claim declaration command {CommandId} succeeded with no insurer reference; the "
                + "dossier stays unacknowledged rather than carrying a blank reference",
                evt.CommandId);
            return;
        }

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandSucceededEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();

        try
        {
            var claim = await db.Claims
                .AsTracking()
                .FirstOrDefaultAsync(c => c.DeclareCommandId == evt.CommandId, ct);

            if (claim is null)
            {
                // A declaration this platform did not open a dossier for — a command replayed
                // after the dossier was purged by ASS-12, or one enqueued by another path. The
                // inbox row is still committed, so the message is not redelivered for ever.
                await db.SaveChangesAsync(ct);
                return;
            }

            // Business-level idempotence, not just inbox-level: two distinct messages can carry
            // the same outcome, and the aggregate refuses a DIFFERENT reference rather than
            // re-pointing a dossier. Catching that here keeps it a logged incident instead of a
            // consumer that dies on every redelivery.
            try
            {
                claim.RecordInsurerReference(evt.ExternalRef, claimNumber: null, clock: clock);
            }
            catch (DomainException ex)
            {
                logger.LogError(
                    "Claim {ClaimId} already carries insurer reference {Existing}; command "
                    + "{CommandId} reported {Incoming}. Not re-pointed: {Reason}",
                    claim.Id, claim.ExternalClaimId, evt.CommandId, evt.ExternalRef, ex.Message);

                await db.SaveChangesAsync(ct);
                return;
            }

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Claim {ClaimId} acknowledged by the insurer as {ExternalClaimId}",
                claim.Id, evt.ExternalRef);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not record the insurer's reference for claim command {CommandId}",
                evt.CommandId);
        }
    }
}
