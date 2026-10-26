namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.HoldingSubscription;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Advances the ASS-05 chain on a SUCCESSFUL command: debit → subscribe → (reversal) → settled.
///
/// <para>
/// <b>One consumer and not three.</b> The three steps arrive as the same event with a different
/// <c>CommandType</c>, and the saga is found the same way each time — by the command id it already
/// stores. Three consumers would mean three inbox keys, three scope set-ups and three places to
/// keep the "which step is this" decision, for a switch that reads in eight lines.
/// </para>
///
/// <para>
/// <b>Every command of the platform publishes this event</b>, including those of the onboarding
/// chain and of BNF-B-16's holdings. A command no saga owns is left alone, silently: that is the
/// normal case, not a miss.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class InsuranceSagaSucceededConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<InsuranceSagaSucceededConsumer> logger)
    : IConsumer<IntegrationCommandSucceededEvent>
{
    internal const string ConsumerKey = nameof(InsuranceSagaSucceededConsumer);

    public async Task Consume(ConsumeContext<IntegrationCommandSucceededEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // Cheap discriminator BEFORE the inbox row, like HoldingSyncOutcomeConsumers: the
        // overwhelming majority of succeeded commands are nothing to do with an insurance saga,
        // and writing an inbox row for each of them would grow a table for no reason.
        if (!IsChainStep(evt.CommandType)) return;

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
        var integration = sp.GetRequiredService<IIntegrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        try
        {
            var saga = await FindSagaAsync(db, evt.CommandId, ct);

            if (saga is null)
            {
                // A command of another chain. The inbox row is committed so the message does not
                // come back, and nothing is logged above debug: this is the common case.
                logger.LogDebug(
                    "Command {CommandId} ({CommandType}) belongs to no insurance saga",
                    evt.CommandId, evt.CommandType);

                await db.SaveChangesAsync(ct);
                return;
            }

            // Only a holding's subscription needs it: the holding is what the insurer is told about.
            var chain = saga.CrmHoldingId is null ? null : sp.GetRequiredService<HoldingSubscriptionChain>();

            await AdvanceAsync(db, integration, chain, clock, saga, evt, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged and dropped. Rethrowing would redeliver against a state the aggregate has
            // already refused once — and the saga's status is readable, which is what ASS-05 asks
            // for so an operator can see a chain that stopped.
            logger.LogError(ex,
                "Could not advance the insurance saga on command {CommandId} ({CommandType})",
                evt.CommandId, evt.CommandType);
        }
    }

    private static bool IsChainStep(string commandType)
        => commandType == nameof(CommandType.DebitAccount)
        || commandType == nameof(CommandType.SubscribePolicy)
        || commandType == nameof(CommandType.ReverseDebit);

    /// <summary>
    /// The saga that owns <paramref name="commandId"/>. One query over three filtered indexes —
    /// the consumer arrives holding a command id and nothing else, which is why those indexes
    /// exist.
    /// </summary>
    private static Task<InsuranceSubscription?> FindSagaAsync(
        IntegrationDbContext db, Guid commandId, CancellationToken ct)
        => db.InsuranceSubscriptions
            .AsTracking()
            .FirstOrDefaultAsync(
                s => s.DebitCommandId == commandId
                  || s.SubscribeCommandId == commandId
                  || s.ReversalCommandId == commandId,
                ct);

    private async Task AdvanceAsync(
        IntegrationDbContext db,
        IIntegrationModule integration,
        HoldingSubscriptionChain? chain,
        TimeProvider clock,
        InsuranceSubscription saga,
        IntegrationCommandSucceededEvent evt,
        CancellationToken ct)
    {
        // Which STEP this is, decided from the saga's own columns rather than from the event's
        // CommandType. The two agree today; the saga is the authority because it is what refuses
        // an out-of-order transition, and a forged or replayed event naming the wrong type must
        // not be able to drive it.
        if (saga.DebitCommandId == evt.CommandId)
        {
            await OnDebitedAsync(db, integration, chain, clock, saga, evt, ct);
            return;
        }

        if (saga.SubscribeCommandId == evt.CommandId)
        {
            await OnIssuedAsync(db, clock, saga, evt, ct);
            return;
        }

        if (saga.ReversalCommandId == evt.CommandId)
        {
            // The premium is back with the customer. ExternalRef is the CBS's reference for the
            // reversal itself — nullable, because a core banking system may acknowledge without
            // issuing one, and a reversal without a reference is still a reversal.
            saga.MarkReversed(evt.ExternalRef, clock);

            logger.LogInformation(
                "Subscription {SubscriptionId}: premium reversed, reference {Reference}",
                saga.Id, evt.ExternalRef ?? "(none)");
        }
    }

    /// <summary>
    /// The money has moved. Now, and only now, the insurer is asked — see the command's own
    /// remarks for why this order and not the other.
    /// </summary>
    private async Task OnDebitedAsync(
        IntegrationDbContext db,
        IIntegrationModule integration,
        HoldingSubscriptionChain? chain,
        TimeProvider clock,
        InsuranceSubscription saga,
        IntegrationCommandSucceededEvent evt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ExternalRef))
        {
            // A debit acknowledged with no reference is unreversible: ReverseDebitAsync is keyed
            // on the original reference, which is the only form a CBS accepts twice safely. So the
            // chain STOPS here rather than subscribing a policy whose premium could never be
            // refunded if the insurer refused it.
            saga.MarkDebitFailed(
                "DEBIT_REFERENCE_MISSING",
                "The core banking system accepted the debit without returning a reference, so a "
                + "reversal could not be keyed on it.",
                clock);

            logger.LogError(
                "Subscription {SubscriptionId}: debit succeeded with no reference; the chain is "
                + "stopped because the premium could not be reversed",
                saga.Id);

            await RejectHoldingAfterSaveAsync(db, chain, saga,
                "Le prélèvement de la prime a été accepté sans référence : la souscription est "
                + "arrêtée, un opérateur doit vérifier le compte du client.", ct);
            return;
        }

        saga.MarkDebited(evt.ExternalRef, clock);

        // The insurer's own code and periodicity live on the catalogue entry, not on the saga:
        // the saga is the audit of one attempt and the product is what describes the contract.
        var product = await db.InsuranceProducts
            .FirstOrDefaultAsync(p => p.Id == saga.ProductId, ct);

        if (product is null)
        {
            // The catalogue entry was deleted between the subscription and the debit. The premium
            // has moved, so the chain cannot simply stop — and recording a DEBT would be the wrong
            // remedy: MarkRefundDue is reachable only from Reversing, because a refund that was
            // never asked of the core banking system is a refund nobody is processing. The money
            // goes back the same way an insurer refusal sends it back.
            await ReverseAfterDebitAsync(
                integration, clock, saga,
                "INSURANCE_PRODUCT_NOT_FOUND",
                "The catalogue entry disappeared after the premium was debited, so the insurer "
                + "could not be asked.",
                ct);

            logger.LogError(
                "Subscription {SubscriptionId}: product {ProductId} vanished after the debit; "
                + "a reversal of {Amount} {Currency} was queued",
                saga.Id, saga.ProductId, saga.PremiumAmount, saga.Currency);

            await RejectHoldingAfterSaveAsync(db, chain, saga,
                "Le produit d'assurance a été retiré après le prélèvement : la prime est remboursée.", ct);
            return;
        }

        // A holding's subscription: the holding's terms and clause, the declarations as signed.
        if (saga.CrmHoldingId is { } holdingId && chain is not null)
        {
            var holding = await chain.HoldingAsync(saga, ct);

            if (holding is null)
            {
                await ReverseAfterDebitAsync(
                    integration, clock, saga,
                    "INSURANCE_HOLDING_NOT_FOUND",
                    "The holding disappeared after the premium was debited, so the insurer could not be asked.",
                    ct);
                return;
            }

            var holdingCommand = await integration.Insurance.RequestPolicySubscriptionAsync(
                await chain.PayloadAsync(holding, saga, product, ct), ct);

            saga.AttachSubscribeCommand(holdingCommand.Value, clock);
            await db.SaveChangesAsync(ct);
            await chain.MarkAnswersTransmittedAsync(saga, ct);

            logger.LogInformation(
                "Subscription {SubscriptionId}: premium debited ({Reference}), insurer asked for "
                + "holding {HoldingId} as command {CommandId}",
                saga.Id, evt.ExternalRef, holdingId, holdingCommand.Value);
            return;
        }

        // The payload carries what the insurer needs and NOT the beneficiaries: those are
        // encrypted on the saga, and decrypting them here would put third parties' names through
        // a second code path. The holding-driven route of BNF-B-16 is what sends a clause, and it
        // reads it from M01 for exactly that reason.
        var commandId = await integration.Insurance.RequestPolicySubscriptionAsync(
            new InsurancePolicyPayload(
                CrmCustomerId: saga.CrmCustomerId,
                CrmProductId: saga.ProductId,
                InsurerProductCode: product.InsurerProductCode,
                EffectiveDate: saga.EffectiveDate,
                PremiumAmount: saga.PremiumAmount,
                Currency: saga.Currency,
                Periodicity: product.Periodicity,
                Beneficiaries: [],
                LinkedLoanReference: null,
                ConsentEvidenceRef: null),
            ct);

        saga.AttachSubscribeCommand(commandId.Value, clock);

        logger.LogInformation(
            "Subscription {SubscriptionId}: premium debited ({Reference}), insurer asked as "
            + "command {CommandId}",
            saga.Id, evt.ExternalRef, commandId.Value);
    }

    /// <summary>
    /// Sends the premium back, from the <c>Debited</c> state. Shared with the rejection
    /// consumer's own reversal in spirit but not in code: that one is driven by an insurer's
    /// refusal and carries its error family, this one by a catalogue entry that disappeared.
    ///
    /// <para>
    /// The enqueue comes first and the aggregate is told second, because <c>BeginReversal</c>
    /// needs the command id — the same order the debit step follows at creation.
    /// </para>
    /// </summary>
    /// <summary>
    /// The chain stopped before the insurer was asked, so no answer will ever reach the holding:
    /// the subscription's state is saved first, then the holding is told why.
    /// </summary>
    private static async Task RejectHoldingAfterSaveAsync(
        IntegrationDbContext db, HoldingSubscriptionChain? chain, InsuranceSubscription saga, string reason, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);

        if (chain is not null)
            await chain.RejectHoldingAsync(saga, reason, ct);
    }

    private async Task ReverseAfterDebitAsync(
        IIntegrationModule integration,
        TimeProvider clock,
        InsuranceSubscription saga,
        string code,
        string detail,
        CancellationToken ct)
    {
        // Both are set by now: MarkDebited refuses an empty reference, and the account came from
        // the subscription request. The guard is belt and braces for a state machine somebody
        // later loosens.
        if (saga.CbsDebitReference is not { Length: > 0 } reference
            || saga.CbsAccountRef is not { Length: > 0 } account)
        {
            logger.LogError(
                "Subscription {SubscriptionId}: {Code} after the debit, and no reversal could be "
                + "keyed; the customer is owed {Amount} {Currency} and an operator must act",
                saga.Id, code, saga.PremiumAmount, saga.Currency);

            return;
        }

        var commandId = await integration.CoreBanking.RequestDebitReversalAsync(
            new DebitReversalRequest(
                SubscriptionId: saga.Id,
                AccountRef: account,
                OriginalReference: reference,
                Attempt: saga.ReversalAttempts + 1),
            ct);

        saga.BeginReversal(commandId.Value, code, detail, clock);
    }

    /// <summary>
    /// The insurer issued the contract. The saga records its reference and a
    /// <see cref="PolicyRecord"/> is created so the policy appears on the customer's 360 view
    /// without waiting for ASS-07's nightly synchronisation.
    /// </summary>
    private async Task OnIssuedAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        InsuranceSubscription saga,
        IntegrationCommandSucceededEvent evt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ExternalRef))
        {
            // Same shape as BNF-B-16's rule for a holding: a success with no external reference
            // leaves the saga where it is, because the reference is the only link between the two
            // systems and a policy without one is irreconcilable. Deliberately NOT a failure —
            // the premium is collected and the insurer said yes; what is missing is the number,
            // which ASS-07's synchronisation can still bring.
            logger.LogWarning(
                "Subscription {SubscriptionId}: the insurer accepted without returning a policy "
                + "reference, so the saga stays in {Status} for the synchronisation to resolve",
                saga.Id, saga.Status);

            return;
        }

        // The product's periodicity until ASS-07's synchronisation brings the insurer's own
        // answer. It used to be Single, « one premium collected, no schedule known » — false
        // since a contract can be issued with its premium still to pay (insurance lot 2), and
        // a monthly contract read « Prime unique » to the agent.
        var periodicity = await db.InsuranceProducts
            .Where(p => p.Id == saga.ProductId)
            .Select(p => (PremiumPeriodicity?)p.Periodicity)
            .FirstOrDefaultAsync(ct) ?? PremiumPeriodicity.Single;

        var policy = PolicyRecord.Create(
            tenantId: saga.TenantId,
            connectionId: saga.ConnectionId,
            crmCustomerId: saga.CrmCustomerId,
            externalPolicyId: evt.ExternalRef,
            // The insurer's own number, as far as we know it at this point: the reference IS the
            // number until a synchronisation says otherwise, and ApplySync is what corrects it.
            policyNumber: evt.ExternalRef,
            insurerProductCode: string.Empty,
            status: PolicyStatus.Issued,
            effectiveDate: saga.EffectiveDate,
            premiumAmount: saga.PremiumAmount,
            currency: saga.Currency,
            periodicity: periodicity,
            nextDueDate: PolicyRecord.FirstDueDate(saga.EffectiveDate, periodicity, firstPremiumPaid: !saga.IsPremiumOutstanding),
            clock: clock,
            productId: saga.ProductId,
            subscriptionId: saga.Id);

        db.Policies.Add(policy);

        saga.MarkIssued(policy.Id, evt.ExternalRef, clock);

        logger.LogInformation(
            "Subscription {SubscriptionId}: policy {PolicyReference} issued and recorded as {PolicyId}",
            saga.Id, evt.ExternalRef, policy.Id);
    }
}

/// <summary>
/// Advances the ASS-05 chain on a REJECTED command — which is where the compensation lives.
///
/// <para>
/// Three outcomes, and the middle one is the reason the saga exists at all: the insurer refusing
/// AFTER the premium was collected means the money has to go back, and nothing but a durable row
/// can remember that it owes a reversal across a process restart.
/// </para>
/// </summary>
public sealed class InsuranceSagaRejectedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<InsuranceSagaRejectedConsumer> logger)
    : IConsumer<IntegrationCommandRejectedEvent>
{
    internal const string ConsumerKey = nameof(InsuranceSagaRejectedConsumer);

    public async Task Consume(ConsumeContext<IntegrationCommandRejectedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        if (evt.CommandType != nameof(CommandType.DebitAccount)
            && evt.CommandType != nameof(CommandType.SubscribePolicy)
            && evt.CommandType != nameof(CommandType.ReverseDebit))
            return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandRejectedEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var integration = sp.GetRequiredService<IIntegrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        try
        {
            var saga = await db.InsuranceSubscriptions
                .AsTracking()
                .FirstOrDefaultAsync(
                    s => s.DebitCommandId == evt.CommandId
                      || s.SubscribeCommandId == evt.CommandId
                      || s.ReversalCommandId == evt.CommandId,
                    ct);

            if (saga is null)
            {
                logger.LogDebug(
                    "Rejected command {CommandId} ({CommandType}) belongs to no insurance saga",
                    evt.CommandId, evt.CommandType);

                await db.SaveChangesAsync(ct);
                return;
            }

            await CompensateAsync(integration, clock, saga, evt, ct);
            await db.SaveChangesAsync(ct);

            // A refused debit never reaches the insurer, so the holding hears nothing from that
            // side: it is told here. An insurer refusal is not — that answer is addressed to the
            // holding and HoldingSyncRejectedConsumer records it.
            if (saga.Status == SubscriptionStatus.DebitFailed && saga.CrmHoldingId is not null)
            {
                await sp.GetRequiredService<HoldingSubscriptionChain>().RejectHoldingAsync(
                    saga,
                    $"Prélèvement de la prime refusé ({evt.ErrorCode}). La souscription n'a pas été "
                    + "envoyée à l'assureur.",
                    ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not compensate the insurance saga on rejected command {CommandId}",
                evt.CommandId);
        }
    }

    private async Task CompensateAsync(
        IIntegrationModule integration,
        TimeProvider clock,
        InsuranceSubscription saga,
        IntegrationCommandRejectedEvent evt,
        CancellationToken ct)
    {
        // The CBS refused the premium. Nothing has moved, so there is nothing to compensate: the
        // saga simply stops, and the customer is not subscribed.
        if (saga.DebitCommandId == evt.CommandId)
        {
            saga.MarkDebitFailed(evt.ErrorCode, evt.Detail, clock);

            logger.LogWarning(
                "Subscription {SubscriptionId}: the premium debit was refused ({Family}/{Code})",
                saga.Id, evt.ErrorFamily, evt.ErrorCode);

            return;
        }

        // The one that matters: the money has moved and the insurer said no.
        if (saga.SubscribeCommandId == evt.CommandId)
        {
            // Sent before its premium: nothing was collected, so nothing goes back.
            if (saga.Status == SubscriptionStatus.AwaitingPremium)
            {
                saga.MarkRefused(evt.ErrorCode, evt.Detail, clock);
                return;
            }

            // A premium the agent collected has no core banking debit to reverse: the institution
            // owes it back from the moment the insurer says no.
            if (saga.Status == SubscriptionStatus.PaymentRecorded)
            {
                saga.MarkRefundDue(evt.ErrorCode, evt.Detail, clock);

                logger.LogWarning(
                    "Subscription {SubscriptionId}: the insurer refused ({Family}/{Code}) a premium "
                    + "recorded by the agent; {Amount} {Currency} is owed back to the client",
                    saga.Id, evt.ErrorFamily, evt.ErrorCode, saga.PremiumAmount, saga.Currency);
                return;
            }

            await ReverseAsync(integration, clock, saga, evt, ct);
            return;
        }

        if (saga.ReversalCommandId == evt.CommandId)
        {
            // The CBS refused the reversal. NOT retried automatically, deliberately: a refund the
            // core banking system has already refused once will be refused again for the same
            // reason, and a retry loop would only bury the fact that a customer is owed money.
            // PremiumRefundDue is the state an operator works from, and ReversalAttempts tells
            // them the CBS has said no.
            saga.MarkRefundDue(evt.ErrorCode, evt.Detail, clock);

            logger.LogError(
                "Subscription {SubscriptionId}: the premium reversal was REFUSED ({Family}/{Code}); "
                + "the customer is owed {Amount} {Currency} and an operator must act",
                saga.Id, evt.ErrorFamily, evt.ErrorCode, saga.PremiumAmount, saga.Currency);
        }
    }

    private async Task ReverseAsync(
        IIntegrationModule integration,
        TimeProvider clock,
        InsuranceSubscription saga,
        IntegrationCommandRejectedEvent evt,
        CancellationToken ct)
    {
        if (saga.CbsDebitReference is not { Length: > 0 } reference
            || saga.CbsAccountRef is not { Length: > 0 } account)
        {
            // UNREACHABLE by construction, and kept as defence in depth rather than removed.
            //
            // To be here the saga holds a SubscribeCommandId; AttachSubscribeCommand requires the
            // Debited state; and MarkDebited refuses an empty reference. So a saga that reached
            // the insurer always has one. The branch exists for the day somebody loosens that
            // state machine — the alternative is a NullReference in the code path that refunds a
            // customer. No transition is attempted, because every one the aggregate offers from
            // here would be refused: what is left is to say so loudly.
            //
            // SagaInvariantTests pins the three rules that make this unreachable, so the day one
            // of them is relaxed the comment above stops being true and a test says so.
            logger.LogError(
                "Subscription {SubscriptionId}: the insurer refused and no reversal could be "
                + "keyed; the customer is owed {Amount} {Currency} and an operator must act",
                saga.Id, saga.PremiumAmount, saga.Currency);

            return;
        }

        // BeginReversal needs the command id, which only exists once the command is enqueued —
        // so the enqueue comes first and the aggregate is told second, exactly as the debit step
        // does at creation.
        var attempt = saga.ReversalAttempts + 1;

        var commandId = await integration.CoreBanking.RequestDebitReversalAsync(
            new DebitReversalRequest(
                SubscriptionId: saga.Id,
                AccountRef: account,
                OriginalReference: reference,
                Attempt: attempt),
            ct);

        saga.BeginReversal(commandId.Value, evt.ErrorCode, evt.Detail, clock);

        logger.LogWarning(
            "Subscription {SubscriptionId}: the insurer refused ({Family}/{Code}); reversal "
            + "attempt {Attempt} queued as command {CommandId}",
            saga.Id, evt.ErrorFamily, evt.ErrorCode, attempt, commandId.Value);
    }
}
