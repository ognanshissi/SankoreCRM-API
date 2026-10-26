namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Retention;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using KycStatus = Sankore.Modules.Kyc.PublicApi.KycStatus;

/// <summary>
/// Attaches a <see cref="SubscriptionStatus.Prepared"/> subscription to a signed holding, with its
/// consent proof and the questionnaire answers as signed — all in ONE transaction, for the reason
/// the command's remarks give. Nothing is debited and nothing reaches the insurer here.
///
/// <para>
/// Every condition is checked NOW, in front of the agent, rather than when the holding is sent: a
/// refusal at sending time arrives minutes later as a rejected holding, and the client has left the
/// counter by then.
/// </para>
/// </summary>
internal sealed class SubscribeInsurancePolicyHandler(
    IntegrationDbContext db,
    ICustomersModule customers,
    IKycModule kyc,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule administration,
    ILeadsModule leads,
    CrmProductCatalogue crmProducts,
    IFieldEncryptor encryptor,
    ICurrentUser currentUser,
    IOptions<InsuranceRetentionOptions> retention,
    TimeProvider clock,
    ILogger<SubscribeInsurancePolicyHandler> logger)
    : IRequestHandler<SubscribeInsurancePolicyCommand, Result<SubscribeInsurancePolicyResult>>
{
    /// <summary>The questionnaire row's code for the answers a won project carries.</summary>
    internal const string ProjectQuestionnaireCode = "PROJECT_QUESTIONNAIRES";

    public async Task<Result<SubscribeInsurancePolicyResult>> Handle(
        SubscribeInsurancePolicyCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // ── The holding: the client's, signed, and visible to this agent ─────
        var holding = await Suppressed(() => customers.GetHoldingForSyncAsync(tenantId, cmd.HoldingId, ct));

        if (holding is null
            || !await Suppressed(() => customers.CanActorAccessClientAsync(tenantId, holding.ClientId, currentUser.Id, ct)))
            return Fail(InsuranceErrors.HoldingNotFound);

        // ── The product and its insurer ─────────────────────────────────────
        var row = await db.InsuranceProducts
            .Where(p => p.Id == cmd.ProductId)
            .Join(db.Connections, p => p.ConnectionId, c => c.Id, (p, c) => new { Product = p, Connection = c })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return Fail(InsuranceErrors.ProductNotFound);

        // The insurer's product must realise the holding's catalogue line: subscribing a holding
        // on « Assurance obsèques » to an insurer's loan cover would send one contract and record
        // another.
        if (!string.Equals(
                row.Product.CrmProductCode?.Trim(), holding.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail(InsuranceErrors.ProductNotForHolding);

        // Re-checked by the consumer too; refused here so the agent learns it at the counter.
        if (!holding.IsSigned || holding.Status != "PendingSignature")
            return Fail(InsuranceErrors.HoldingNotSigned);

        if (holding.EffectiveFrom is not { } effectiveDate)
            return Fail(InsuranceErrors.EffectiveDateRequired);

        // ── May this product still be sold ──────────────────────────────────
        //
        // Through the SAME helper the catalogue screen reports with: a product the screen shows as
        // unofferable must not be subscribable.
        var crmStatus = await crmProducts.StatusAsync(row.Product.CrmProductCode, ct);

        var reasons = ProductOfferability.Reasons(
            productIsActive: row.Product.IsActive,
            connectionIsActive: row.Connection.IsActive,
            connectionIsInsuranceFamily: row.Connection.Family == IntegrationFamily.Insurance,
            effectiveFrom: row.Product.EffectiveFrom,
            effectiveTo: row.Product.EffectiveTo,
            pricingMode: row.Product.PricingMode,
            insurerPricingAvailable: true,
            crmCatalogue: crmStatus,
            today: today,
            hasInformationSheet: row.Product.CurrentInformationSheetId is not null);

        if (reasons.Count > 0)
            return Fail($"{InsuranceErrors.ProductNotOfferable}:{string.Join(',', reasons)}");

        // ── May this client take it ─────────────────────────────────────────
        //
        // The product's own conditions, which the screen used to display and nobody enforced.
        var eligibility = InsuranceEligibilityRules.Evaluate(
            row.Product, await ApplicantFactsAsync(tenantId, holding.ClientId, effectiveDate, cmd.PaymentMode, ct));

        if (!eligibility.IsEligible)
            return Fail($"{InsuranceErrors.NotEligible}:{string.Join(',', eligibility.Reasons)}");

        // ── May the institution distribute ──────────────────────────────────
        //
        // Its pieces (agrément, garantie financière, RC pro) in force on the day (product owner,
        // 2026-10-25): an expired one refuses NEW subscriptions until renewed.
        var standing = await Suppressed(() => administration.GetInsuranceDistributorStandingAsync(tenantId, today, ct));

        if (!standing.InGoodStanding)
        {
            var detail = !standing.IsConfigured
                ? "NOT_CONFIGURED"
                : string.Join(',', standing.MissingDocuments.Select(k => $"MISSING_{k}")
                    .Concat(standing.ExpiredDocuments.Select(k => $"EXPIRED_{k}")));
            return Fail($"{InsuranceErrors.DistributorNotInGoodStanding}:{detail}");
        }

        // ── The information sheet, handed over before the signature ─────────
        var delivery = await db.InformationSheetDeliveries
            .Where(d => d.HoldingId == holding.HoldingId && d.ProductId == cmd.ProductId)
            .OrderByDescending(d => d.DeliveredAt)
            .FirstOrDefaultAsync(ct);

        if (delivery is null || (holding.SignedAt is { } signedAt && delivery.DeliveredAt > signedAt))
            return Fail(InsuranceErrors.InformationSheetNotDelivered);

        // ── How the premium is paid ─────────────────────────────────────────
        //
        // Collecting in the insurer's name — a debit or a payment the agent takes — needs its
        // express mandate. Without one, the insurer collects and the contract waits for its word.
        if (cmd.PaymentMode is PremiumPaymentMode.CoreBankingDebit or PremiumPaymentMode.RecordedByAgent
            && !row.Connection.HasCollectionMandate)
            return Fail(InsuranceErrors.NoCollectionMandate);

        if (cmd.PaymentMode == PremiumPaymentMode.CoreBankingDebit
            && !await db.Connections.AnyAsync(c => c.Family == IntegrationFamily.CoreBanking && c.IsActive, ct))
            return Fail(InsuranceErrors.CoreBankingUnavailable);

        if (cmd.PaymentMode == PremiumPaymentMode.RecordedByAgent && cmd.PaidOn > today)
            return Fail(InsuranceErrors.PaymentDateInFuture);

        // A caller-supplied amount wins: it is the quote the client was shown and agreed to.
        var premium = cmd.PremiumAmount ?? row.Product.FixedPremiumAmount;

        if (premium is not { } amount || amount <= 0m)
            return Fail(InsuranceErrors.PremiumUnknown);

        var currency = string.IsNullOrWhiteSpace(cmd.Currency) ? row.Product.Currency : cmd.Currency;

        if (string.IsNullOrWhiteSpace(currency))
            return Fail(InsuranceErrors.CurrencyUnknown);

        // The derogation is for large premiums only: above 80 times the annual SMIG.
        if (cmd.PaymentMode == PremiumPaymentMode.DeferredByDerogation
            && (standing.AnnualSmig is not { } smig || amount <= smig * 80m))
            return Fail(InsuranceErrors.DerogationNotAllowed);

        // ── Who pays, when it is not the subscriber (lot 4) ─────────────────
        //
        // An existing client of the institution only, with an approved KYC: the premium's origin is
        // an LBC/FT question, and a payer identified on the spot would not be one.
        if (cmd.PremiumPayerClientId is { } payerId && payerId != holding.ClientId)
        {
            // Within the agent's perimeter, like every other client read: otherwise the answer
            // (not found / KYC not approved) would tell whether a client of another branch exists.
            var payer = await Suppressed(() => customers.GetClientSummaryAsync(tenantId, payerId, ct));
            if (payer is null
                || !await Suppressed(() => customers.CanActorAccessClientAsync(tenantId, payerId, currentUser.Id, ct)))
                return Fail(InsuranceErrors.PayerNotFound);

            if (await Suppressed(() => kyc.GetStatusAsync(tenantId, payerId, ct)) != KycStatus.Approved)
                return Fail(InsuranceErrors.PayerKycNotApproved);
        }

        // ── The won project's questionnaires, as signed ─────────────────────
        ProjectAnswersSnapshot? answers = null;

        if (holding.SourceOpportunityId is { } opportunityId)
        {
            answers = await Suppressed(() => leads.GetProjectAnswersSnapshotAsync(tenantId, opportunityId, ct));

            if (answers is { IsComplete: false })
                return Fail($"{InsuranceErrors.QuestionnairesIncomplete}:{answers.MissingCount}");
        }

        // ── Idempotence ─────────────────────────────────────────────────────
        var live = await db.InsuranceSubscriptions
            .AsTracking()
            .Where(s => s.CrmHoldingId == holding.HoldingId
                     && s.ClauseVersion == holding.ClauseVersion
                     && s.Status != SubscriptionStatus.DebitFailed
                     && s.Status != SubscriptionStatus.Reversed
                     && s.Status != SubscriptionStatus.Cancelled
                     && s.Status != SubscriptionStatus.PremiumRefundDue
                     && s.Status != SubscriptionStatus.Refused)
            .FirstOrDefaultAsync(ct);

        if (live is not null && live.Status != SubscriptionStatus.Prepared)
        {
            // Already left: the premium is collected or being collected. Success, not a conflict.
            return Result.Ok(new SubscribeInsurancePolicyResult(
                live.Id, live.Status.ToString(), AlreadyExisted: true, DebitCommandId: live.DebitCommandId));
        }

        if (live is not null)
        {
            // Prepared and not yet sent: the agent is preparing it again — a corrected payment, a
            // consent re-collected. The old one is abandoned rather than edited, so its proof stays
            // what it was.
            live.Cancel("Remplacée par une nouvelle préparation.", clock);
            await db.SaveChangesAsync(ct);
        }

        var otherHolding = await db.InsuranceSubscriptions
            .AnyAsync(s => s.CrmCustomerId == holding.ClientId
                        && s.ProductId == cmd.ProductId
                        && s.EffectiveDate == effectiveDate
                        && s.CrmHoldingId != holding.HoldingId
                        && s.Status != SubscriptionStatus.DebitFailed
                        && s.Status != SubscriptionStatus.Reversed
                        && s.Status != SubscriptionStatus.Cancelled
                        && s.Status != SubscriptionStatus.PremiumRefundDue
                        && s.Status != SubscriptionStatus.Refused, ct);

        if (otherHolding)
            return Fail(InsuranceErrors.SubscriptionInFlight);

        // ── Write: the subscription, its proof and its answers, together ────
        InsuranceSubscription subscription;

        try
        {
            subscription = InsuranceSubscription.Prepare(
                tenantId: tenantId,
                connectionId: row.Connection.Id,
                crmCustomerId: holding.ClientId,
                productId: cmd.ProductId,
                effectiveDate: effectiveDate,
                premiumAmount: amount,
                currency: currency,
                createdBy: currentUser.Id,
                clock: clock,
                holdingId: holding.HoldingId,
                clauseVersion: holding.ClauseVersion,
                paymentMode: cmd.PaymentMode,
                cbsAccountRef: cmd.AccountRef,
                paymentMethod: cmd.PaymentMethod,
                paymentReference: cmd.PaymentReference,
                paidOn: cmd.PaidOn,
                sourceOpportunityId: holding.SourceOpportunityId,
                answersSha256: answers?.Sha256,
                commitmentReference: cmd.CommitmentReference,
                commitmentSignedOn: cmd.CommitmentSignedOn,
                informationSheetDeliveryId: delivery.Id,
                premiumPayerClientId: cmd.PremiumPayerClientId == holding.ClientId ? null : cmd.PremiumPayerClientId);
        }
        catch (DomainException ex)
        {
            return Fail($"{InsuranceErrors.PaymentInvalid}: {ex.Message}");
        }

        db.InsuranceSubscriptions.Add(subscription);

        // ASS-12's retention, decided at write time.
        var retainUntil = now.AddYears(retention.Value.ResolvedYears);

        db.ConsentProofs.Add(ConsentProof.Create(
            tenantId: tenantId,
            subscriptionId: subscription.Id,
            crmCustomerId: holding.ClientId,
            channel: cmd.ConsentChannel,
            consentTextVersion: cmd.ConsentTextVersion,
            consentedAt: cmd.ConsentedAt,
            capturedBy: currentUser.Id,
            clock: clock,
            consentTextSha256: cmd.ConsentTextSha256,
            evidenceEncrypted: Encrypt(cmd.ConsentEvidence),
            evidenceStorageRef: cmd.ConsentEvidenceStorageRef,
            evidenceSha256: cmd.ConsentEvidenceSha256,
            contentType: cmd.ConsentEvidenceContentType,
            retainUntil: retainUntil));

        if (answers is not null)
        {
            // The copy the client signed, encrypted: it may hold health answers, and it is what the
            // insurer is told when the holding leaves.
            db.MedicalQuestionnaires.Add(MedicalQuestionnaire.Create(
                tenantId: tenantId,
                subscriptionId: subscription.Id,
                crmCustomerId: holding.ClientId,
                questionnaireCode: ProjectQuestionnaireCode,
                answersEncrypted: encryptor.Encrypt(answers.AnswersJson)!,
                capturedBy: currentUser.Id,
                clock: clock,
                requiresMedicalUnderwriting: false,
                retainUntil: retainUntil));
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Subscription {SubscriptionId} prepared for holding {HoldingId} (client {ClientId}) at "
            + "insurer {ConnectionId}, premium by {PaymentMode}",
            subscription.Id, holding.HoldingId, holding.ClientId, row.Connection.Id, cmd.PaymentMode);

        return Result.Ok(new SubscribeInsurancePolicyResult(
            subscription.Id, subscription.Status.ToString(), AlreadyExisted: false, DebitCommandId: null));
    }

    private async Task<ApplicantFacts> ApplicantFactsAsync(
        Guid tenantId, Guid clientId, DateOnly onDate, PremiumPaymentMode paymentMode, CancellationToken ct)
    {
        var facts = await Suppressed(() => customers.GetInsuranceApplicantFactsAsync(tenantId, clientId, ct));
        var status = await Suppressed(() => kyc.GetStatusAsync(tenantId, clientId, ct));

        // Approved is the only status that grants a level; a capped file is the simplified tier.
        var level = KycLevel.None;
        if (status == KycStatus.Approved)
        {
            var limits = await Suppressed(() => kyc.GetLimitsAsync(tenantId, clientId, ct));
            level = limits is { IsCapped: false } ? KycLevel.Full : KycLevel.Simplified;
        }

        return new ApplicantFacts(
            AgeInYears: facts?.BirthDate is { } birth ? AgeOn(birth, onDate) : null,
            KycLevel: level,
            // Known only when the premium is debited from it; otherwise the product's account
            // requirement cannot be confirmed and the rule refuses rather than assumes.
            HasCbsAccount: paymentMode == PremiumPaymentMode.CoreBankingDebit ? true : null,
            HasActiveLoan: facts?.HasActiveLoan);
    }

    private static Result<SubscribeInsurancePolicyResult> Fail(string error)
        => Result.Fail<SubscribeInsurancePolicyResult>(error);

    private static int AgeOn(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (on < birth.AddYears(age)) age--;
        return age;
    }

    /// <summary>
    /// Another module's read, outside this command's transaction: a second connection enlisting
    /// would promote it to a distributed transaction, which Npgsql does not support.
    /// </summary>
    private static async Task<T> Suppressed<T>(Func<Task<T>> read)
    {
        using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var value = await read();
        scope.Complete();
        return value;
    }

    private string? Encrypt(string? clear)
        => string.IsNullOrWhiteSpace(clear) ? null : encryptor.Encrypt(clear);
}

/// <summary>Error codes of this slice. Stable UPPER_SNAKE, like every other code of the module.</summary>
internal static class InsuranceErrors
{
    public const string ProductNotFound = "INSURANCE_PRODUCT_NOT_FOUND";

    /// <summary>Suffixed with the reasons, comma-separated, as the catalogue screen reports them.</summary>
    public const string ProductNotOfferable = "INSURANCE_PRODUCT_NOT_OFFERABLE";

    /// <summary>Refused rather than defaulted to zero: a subscription with no premium is a policy given away.</summary>
    public const string PremiumUnknown = "INSURANCE_PREMIUM_UNKNOWN";

    /// <summary>Refused rather than defaulted: no amount is a sum of money without its currency.</summary>
    public const string CurrencyUnknown = "INSURANCE_CURRENCY_UNKNOWN";

    public const string SubscriptionNotFound = "INSURANCE_SUBSCRIPTION_NOT_FOUND";

    /// <summary>No such holding, or not in the caller's perimeter — the two are not told apart.</summary>
    public const string HoldingNotFound = "INSURANCE_HOLDING_NOT_FOUND";

    /// <summary>The holding is not awaiting submission with a recorded signature.</summary>
    public const string HoldingNotSigned = "INSURANCE_HOLDING_NOT_SIGNED";

    /// <summary>The insurer's product does not realise the holding's catalogue line.</summary>
    public const string ProductNotForHolding = "INSURANCE_PRODUCT_NOT_FOR_HOLDING";

    public const string EffectiveDateRequired = "INSURANCE_EFFECTIVE_DATE_REQUIRED";

    /// <summary>Suffixed with the eligibility reasons (AGE_BELOW_MINIMUM, KYC_LEVEL_INSUFFICIENT…).</summary>
    public const string NotEligible = "INSURANCE_NOT_ELIGIBLE";

    /// <summary>Suffixed with the number of questionnaires still unanswered on the won project.</summary>
    public const string QuestionnairesIncomplete = "INSURANCE_QUESTIONNAIRES_INCOMPLETE";

    /// <summary>A debit was asked for and the tenant has no active core banking connection.</summary>
    public const string CoreBankingUnavailable = "INSURANCE_PAYMENT_CBS_UNAVAILABLE";

    public const string PaymentDateInFuture = "INSURANCE_PAYMENT_DATE_IN_FUTURE";

    /// <summary>The payment half that the mode requires is missing.</summary>
    public const string PaymentInvalid = "INSURANCE_PAYMENT_INVALID";

    /// <summary>Another holding of this client already subscribes this product from that date.</summary>
    public const string SubscriptionInFlight = "INSURANCE_SUBSCRIPTION_IN_FLIGHT";

    /// <summary>The holding of an insurance line was sent with no subscription prepared.</summary>
    public const string NotPrepared = "INSURANCE_SUBSCRIPTION_NOT_PREPARED";

    /// <summary>Suffixed with NOT_CONFIGURED, or MISSING_/EXPIRED_ followed by the piece.</summary>
    public const string DistributorNotInGoodStanding = "INSURANCE_DISTRIBUTOR_NOT_IN_GOOD_STANDING";

    /// <summary>A debit or an agent collection for an insurer that gave no collection mandate.</summary>
    public const string NoCollectionMandate = "INSURANCE_NO_COLLECTION_MANDATE";

    /// <summary>No information sheet of this product was handed to the client before the signature.</summary>
    public const string InformationSheetNotDelivered = "INSURANCE_INFORMATION_SHEET_NOT_DELIVERED";

    /// <summary>The 60-day derogation for a premium at or below 80 times the annual SMIG, or with no SMIG set.</summary>
    public const string DerogationNotAllowed = "INSURANCE_DEROGATION_NOT_ALLOWED";

    /// <summary>The named payer is not a client of this institution.</summary>
    public const string PayerNotFound = "INSURANCE_PAYER_NOT_FOUND";

    /// <summary>The named payer's KYC is not approved.</summary>
    public const string PayerKycNotApproved = "INSURANCE_PAYER_KYC_NOT_APPROVED";

    /// <summary>A questionnaire answer was edited between the signature and the sending.</summary>
    public const string AnswersChanged = "INSURANCE_ANSWERS_CHANGED_SINCE_SIGNATURE";
}
