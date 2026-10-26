namespace Sankore.Modules.Integration.Features.Insurance.HoldingSubscription;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// The steps a holding's insurance subscription shares between the two consumers that drive it:
/// the holding consumer, which starts it when the holding is sent, and the saga consumer, which asks
/// the insurer once the debit is through.
/// </summary>
internal sealed class HoldingSubscriptionChain(
    IntegrationDbContext db,
    IIntegrationModule integration,
    ICustomersModule customers,
    IFieldEncryptor encryptor,
    TimeProvider clock,
    ILogger<HoldingSubscriptionChain> logger,
    ILeadsModule? leads = null)
{
    /// <summary>
    /// Starts a <see cref="SubscriptionStatus.Prepared"/> subscription: the premium is debited, or,
    /// when the agent already collected it, the insurer is asked at once. Saves.
    /// </summary>
    internal async Task StartAsync(InsuranceSubscription saga, HoldingForSync holding, CancellationToken ct)
    {
        // The declaration sent must be the one signed. Compared now, not at the signature: an
        // answer can be edited between the two, and the insurer would then hold a declaration the
        // client never put their name to.
        if (saga.SourceOpportunityId is { } opportunityId && saga.AnswersSha256 is { } signed)
        {
            var current = leads is null ? null : await leads.GetProjectAnswersSnapshotAsync(saga.TenantId, opportunityId, ct);

            if (current?.Sha256 != signed)
            {
                saga.Cancel(InsuranceErrors.AnswersChanged, clock);
                await db.SaveChangesAsync(ct);

                await RejectHoldingAsync(saga,
                    $"{InsuranceErrors.AnswersChanged}: les réponses aux questionnaires ont changé "
                    + "depuis la signature. Préparez à nouveau la souscription.", ct);
                return;
            }
        }

        var product = await db.InsuranceProducts.FirstOrDefaultAsync(p => p.Id == saga.ProductId, ct);

        try
        {
            if (saga.PaymentMode == PremiumPaymentMode.CoreBankingDebit)
            {
                var debit = await integration.CoreBanking.RequestPremiumDebitAsync(
                    new PremiumDebitRequest(
                        SubscriptionId: saga.Id,
                        AccountRef: saga.CbsAccountRef!,
                        Amount: saga.PremiumAmount,
                        Currency: saga.Currency,
                        // Free of personal data: it appears on the client's statement.
                        Label: $"Prime {product?.InsurerProductCode ?? "assurance"}"),
                    ct);

                saga.StartWithDebit(debit.Value, clock);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (product is null)
                throw new DomainException($"{InsuranceErrors.ProductNotFound}: the product was removed before sending.");

            // The premium was collected by the agent, or is collected later: the insurer is asked
            // now either way. Only the first is « paid »; the others leave the holding waiting.
            if (saga.PaymentMode == PremiumPaymentMode.RecordedByAgent)
                saga.StartWithRecordedPayment(clock);
            else
                saga.StartAwaitingPremium(clock);

            var command = await integration.Insurance.RequestPolicySubscriptionAsync(
                await PayloadAsync(holding, saga, product, ct), ct);

            saga.AttachSubscribeCommand(command.Value, clock);
            await db.SaveChangesAsync(ct);

            await MarkAnswersTransmittedAsync(saga, ct);
        }
        catch (DomainException ex)
        {
            // No core banking connection any more, no insurer for the product: nothing has moved,
            // so the subscription is abandoned and the holding says why.
            db.ChangeTracker.Clear();

            var reloaded = await db.InsuranceSubscriptions.AsTracking().FirstAsync(s => s.Id == saga.Id, ct);
            if (reloaded.IsCancellable)
            {
                reloaded.Cancel(ex.Message, clock);
                await db.SaveChangesAsync(ct);
            }

            await RejectHoldingAsync(saga, ex.Message, ct);
        }
    }

    /// <summary>
    /// What the insurer receives for a holding's subscription: the holding's terms and clause, with
    /// the insurer's own product code, the premium the client agreed to, and the declarations as
    /// signed.
    /// </summary>
    internal async Task<InsurancePolicyPayload> PayloadAsync(
        HoldingForSync holding, InsuranceSubscription saga, InsuranceProduct product, CancellationToken ct)
        => HoldingPolicyPayload.From(holding) with
        {
            InsurerProductCode = product.InsurerProductCode,
            EffectiveDate = saga.EffectiveDate,
            PremiumAmount = saga.PremiumAmount,
            Currency = saga.Currency,
            Periodicity = product.Periodicity,
            DeclarationsJson = await DeclarationsAsync(saga.Id, ct),
            PremiumCollection = saga.PaymentMode switch
            {
                PremiumPaymentMode.InsurerCollects => "InsurerCollects",
                PremiumPaymentMode.DeferredByDerogation => "Deferred60Days",
                _ => "CollectedByDistributor",
            },
            PremiumPayerCrmId = saga.PremiumPayerClientId,
        };

    internal Task<HoldingForSync?> HoldingAsync(InsuranceSubscription saga, CancellationToken ct)
        => saga.CrmHoldingId is { } holdingId
            ? customers.GetHoldingForSyncAsync(saga.TenantId, holdingId, ct)
            : Task.FromResult<HoldingForSync?>(null);

    /// <summary>Called once the insurer has been asked: the answers can no longer be edited.</summary>
    internal async Task MarkAnswersTransmittedAsync(InsuranceSubscription saga, CancellationToken ct)
    {
        if (leads is null || saga.SourceOpportunityId is not { } opportunityId)
            return;

        try
        {
            await leads.MarkProjectAnswersTransmittedAsync(saga.TenantId, opportunityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The subscription has left; failing to freeze the copy in M13 must not undo that. The
            // signed copy is on the subscription either way.
            logger.LogWarning(ex,
                "Subscription {SubscriptionId}: the project's answers could not be marked as transmitted",
                saga.Id);
        }
    }

    /// <summary>The holding of a subscription that stopped before the insurer could answer.</summary>
    internal async Task RejectHoldingAsync(InsuranceSubscription saga, string reason, CancellationToken ct)
    {
        if (saga.CrmHoldingId is not { } holdingId)
            return;

        var result = await customers.RejectHoldingSyncAsync(saga.TenantId, holdingId, reason, ct);

        if (result.IsFailure)
            logger.LogWarning(
                "Holding {HoldingId} could not be marked rejected after subscription {SubscriptionId} stopped: {Error}",
                holdingId, saga.Id, result.Error);
    }

    private async Task<string?> DeclarationsAsync(Guid subscriptionId, CancellationToken ct)
    {
        var encrypted = await db.MedicalQuestionnaires
            .Where(q => q.SubscriptionId == subscriptionId)
            .Select(q => q.AnswersEncrypted)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrEmpty(encrypted) ? null : encryptor.Decrypt(encrypted);
    }
}
