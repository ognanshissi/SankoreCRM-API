namespace Sankore.Modules.Integration.Features.Insurance.HoldingSubscription;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// BNF-B-16: a signed <c>ProductHolding</c> queued for its back office becomes an
/// <c>IntegrationCommand</c>.
///
/// <para>
/// A QUEUED command and not a call, like every other write of this module: the insurer may be
/// down, may be a batch file, or may take thirty seconds. The dispatcher of INT-06 delivers it out
/// of band with the retry policy, the error families and the rejection queue that already exist —
/// <b>none of which is rewritten here</b>. This consumer's whole job is to turn an M01 event into
/// one row.
/// </para>
///
/// <para>
/// <b>The event carries ids only</b>, so the terms and the clause are read back through
/// <see cref="ICustomersModule.GetHoldingForSyncAsync"/>. That is deliberate on both sides: a
/// beneficiary clause names who receives a capital, and an outbox row holding it would be a second
/// copy of the contract that nothing ever purges.
/// </para>
/// </summary>
public sealed class HoldingSubscriptionConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<HoldingSubscriptionConsumer> logger)
    : IConsumer<ProductSubscriptionRequestedEvent>
{
    /// <summary>
    /// What distinguishes this consumer's inbox rows. Internal so the idempotency test can name
    /// the row it expects rather than re-deriving the hash.
    /// </summary>
    internal const string ConsumerKey = nameof(HoldingSubscriptionConsumer);

    public async Task Consume(ConsumeContext<ProductSubscriptionRequestedEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // The ambient SYSTEM scope BEFORE the DI scope: ITenantContext and ICurrentUser are built
        // from it, the facade reads the tenant off ICurrentUser, and a consumer has no HTTP
        // context. The requesting agent is the actor, so the command names somebody accountable.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.RequestedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        // A NEW subscription leaves for an insurer only while the tenant runs « Assurance ». The
        // holding stays PendingSync, as on any refusal before the queue, and is not replayed when
        // the module comes back. Contracts already at the insurer are not touched.
        if (!await sp.GetRequiredService<ITenantModules>()
                .IsEnabledAsync(evt.TenantId, TenantModules.Insurance, ct))
        {
            logger.LogInformation(
                "Assurance is off for tenant {TenantId}: holding {HoldingId} is not sent to an insurer",
                evt.TenantId, evt.HoldingId);
            return;
        }

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(ProductSubscriptionRequestedEvent),
                ConsumerKey,
                ct))
            return;

        var customers = sp.GetRequiredService<ICustomersModule>();
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var integration = sp.GetRequiredService<IIntegrationModule>();

        try
        {
            var holding = await customers.GetHoldingForSyncAsync(evt.TenantId, evt.HoldingId, ct);

            if (holding is null)
            {
                // Closed or deleted between the publication and here. Not an incident: the inbox
                // row is committed, the event will not come back, and there is nothing to send.
                logger.LogInformation(
                    "Holding {HoldingId} (tenant {TenantId}) no longer exists; nothing queued",
                    evt.HoldingId, evt.TenantId);
                return;
            }

            // ── The signature, re-checked here (BNF-B-16) ────────────────────
            //
            // The transition to PendingSync already required it, inside M01's own transaction. It
            // is checked again because this is the last code that runs before a contract leaves
            // the institution, and a consumer that pushes one must not take a second-hand word for
            // the client's consent. A refusal here is loud: somebody has changed the holding's
            // state machine, or the event was forged.
            if (!holding.IsSigned)
            {
                logger.LogError(
                    "Holding {HoldingId} (tenant {TenantId}) reached the subscription consumer "
                    + "UNSIGNED and was not queued. Nothing may be sent to a back office without "
                    + "a recorded signature.",
                    evt.HoldingId, evt.TenantId);
                return;
            }

            // ── An insurance line: its subscription carries the payment ──────
            //
            // A holding whose line an insurer's product realises is sent through its prepared
            // subscription — premium first, then the insurer — never straight to the insurer, which
            // is how a policy used to become Active with no premium collected.
            var prepared = await db.InsuranceSubscriptions
                .AsTracking()
                .FirstOrDefaultAsync(
                    s => s.CrmHoldingId == holding.HoldingId
                      && s.ClauseVersion == holding.ClauseVersion
                      && s.Status == SubscriptionStatus.Prepared,
                    ct);

            if (prepared is not null)
            {
                await sp.GetRequiredService<HoldingSubscriptionChain>().StartAsync(prepared, holding, ct);

                logger.LogInformation(
                    "Holding {HoldingId} (tenant {TenantId}) started subscription {SubscriptionId} ({Status})",
                    evt.HoldingId, evt.TenantId, prepared.Id, prepared.Status);
                return;
            }

            var code = holding.ProductCode.Trim().ToUpperInvariant();

            if (await db.InsuranceProducts.AnyAsync(p => p.CrmProductCode == code, ct))
            {
                var rejected = await customers.RejectHoldingSyncAsync(
                    evt.TenantId, holding.HoldingId,
                    $"{InsuranceErrors.NotPrepared}: le paiement de la prime et le consentement n'ont "
                    + "pas été enregistrés. Finalisez-les avec « Envoyer à l'assureur » sur la page du contrat.",
                    ct);

                logger.LogWarning(
                    "Holding {HoldingId} (tenant {TenantId}) is on an insurance line and was sent with "
                    + "no prepared subscription; rejected ({Outcome})",
                    evt.HoldingId, evt.TenantId, rejected.IsSuccess ? "recorded" : rejected.Error);
                return;
            }

            // A CRM-only line. Null is also what an unreachable catalogue yields, and both mean
            // the same thing here: do not send. A subscription not yet pushed can be re-queued by
            // hand; one pushed to the wrong system cannot be recalled.
            if (string.IsNullOrWhiteSpace(holding.ExternalSystem))
            {
                logger.LogInformation(
                    "Holding {HoldingId} (tenant {TenantId}) is on a CRM-only product line "
                    + "(or the catalogue was unreachable); nothing queued",
                    evt.HoldingId, evt.TenantId);
                return;
            }

            var payload = HoldingPolicyPayload.From(holding);

            var commandId = await integration.Insurance.RequestPolicySubscriptionAsync(payload, ct);

            // The facade enlists in the caller's unit of work and deliberately does not save, so
            // this consumer IS the caller and must commit. The inbox row was committed by the
            // guard above.
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Holding {HoldingId} clause v{ClauseVersion} (tenant {TenantId}) queued as "
                + "integration command {CommandId}",
                evt.HoldingId, holding.ClauseVersion, evt.TenantId, commandId.Value);
        }
        catch (DomainException ex)
        {
            // The facade's refusals, and none is an incident of this consumer: the tenant has no
            // active insurance connection, or the product line is mapped to none of several. Both
            // are an administrator's to clear, and rethrowing would have the broker redeliver for
            // ever against a condition only a human can change. The holding stays in PendingSync,
            // which is exactly where a reconciliation run (BNF-B-17) looks for it.
            logger.LogWarning(
                "Holding {HoldingId} (tenant {TenantId}) was not queued: {Reason}",
                evt.HoldingId, evt.TenantId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not queue the subscription of holding {HoldingId} (tenant {TenantId})",
                evt.HoldingId, evt.TenantId);
        }
    }
}
