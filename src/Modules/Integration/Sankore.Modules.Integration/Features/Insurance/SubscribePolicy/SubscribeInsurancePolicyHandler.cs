namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Retention;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the saga and queues its first step. Everything written here lands in ONE transaction —
/// the subscription, its consent proof, its medical questionnaire and the debit command row — for
/// the reason the command's own remarks give: consent collected and not stored is worse than
/// consent not taken.
/// </summary>
internal sealed class SubscribeInsurancePolicyHandler(
    IntegrationDbContext db,
    IIntegrationModule integration,
    CrmProductCatalogue crmProducts,
    IFieldEncryptor encryptor,
    ICurrentUser currentUser,
    IOptions<InsuranceRetentionOptions> retention,
    TimeProvider clock,
    ILogger<SubscribeInsurancePolicyHandler> logger)
    : IRequestHandler<SubscribeInsurancePolicyCommand, Result<SubscribeInsurancePolicyResult>>
{
    public async Task<Result<SubscribeInsurancePolicyResult>> Handle(
        SubscribeInsurancePolicyCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // The product and its connection together: offerability is half the product's and half the
        // connection's, so a product loaded without its insurer cannot be judged.
        var row = await db.InsuranceProducts
            .Where(p => p.Id == cmd.ProductId)
            .Join(db.Connections, p => p.ConnectionId, c => c.Id, (p, c) => new { Product = p, Connection = c })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return Result.Fail<SubscribeInsurancePolicyResult>(InsuranceErrors.ProductNotFound);

        // ── May this product still be sold ──────────────────────────────────
        //
        // Through the SAME helper the catalogue screen reports with, not a second set of
        // conditions: a product the screen shows as unofferable must not be subscribable, and two
        // implementations of "offerable" would eventually disagree about which.
        var crmStatus = await crmProducts.StatusAsync(row.Product.CrmProductCode, ct);

        var reasons = ProductOfferability.Reasons(
            productIsActive: row.Product.IsActive,
            connectionIsActive: row.Connection.IsActive,
            connectionIsInsuranceFamily: row.Connection.Family == IntegrationFamily.Insurance,
            effectiveFrom: row.Product.EffectiveFrom,
            effectiveTo: row.Product.EffectiveTo,
            pricingMode: row.Product.PricingMode,
            // Pricing by the insurer is irrelevant here: a caller who supplied a premium has
            // already quoted, and one who did not falls back to the catalogue below. Passing true
            // keeps this check about the product's SALEABILITY, which is what it is for.
            insurerPricingAvailable: true,
            crmCatalogue: crmStatus,
            today: today);

        if (reasons.Count > 0)
        {
            return Result.Fail<SubscribeInsurancePolicyResult>(
                $"{InsuranceErrors.ProductNotOfferable}:{string.Join(',', reasons)}");
        }

        // ── The premium ─────────────────────────────────────────────────────
        //
        // A caller-supplied amount wins: it is the quote the customer was shown and agreed to, and
        // re-deriving it here could debit a figure nobody signed. Falling back to the catalogue is
        // what a fixed-price product needs.
        var premium = cmd.PremiumAmount ?? row.Product.FixedPremiumAmount;

        if (premium is not { } amount || amount <= 0m)
        {
            return Result.Fail<SubscribeInsurancePolicyResult>(InsuranceErrors.PremiumUnknown);
        }

        // The product's currency is NULLABLE — an insurer-priced entry may carry none until a
        // quote names one — so the caller's wins and the absence of both is refused rather than
        // defaulted. A platform guess here would debit a West-African account in the wrong unit.
        var currency = string.IsNullOrWhiteSpace(cmd.Currency)
            ? row.Product.Currency
            : cmd.Currency;

        if (string.IsNullOrWhiteSpace(currency))
            return Result.Fail<SubscribeInsurancePolicyResult>(InsuranceErrors.CurrencyUnknown);

        // ── Idempotence ─────────────────────────────────────────────────────
        //
        // Read first so a retry gets the existing row instead of a 500 from
        // ux_ins_subscription_idempotency. The index is still what makes the rule true under
        // concurrency — EF InMemory honours no unique index, so this read is the only half the
        // test suite can prove.
        var existing = await db.InsuranceSubscriptions
            .FirstOrDefaultAsync(
                s => s.CrmCustomerId == cmd.CrmCustomerId
                  && s.ProductId == cmd.ProductId
                  && s.EffectiveDate == cmd.EffectiveDate,
                ct);

        if (existing is not null)
        {
            // Success, not a conflict: the caller asked for the customer to be subscribed and the
            // customer is being subscribed. A second debit is precisely what the index exists to
            // prevent.
            logger.LogInformation(
                "Subscription {SubscriptionId} already in flight for customer {CustomerId}, "
                + "product {ProductId}, effective {EffectiveDate}",
                existing.Id, cmd.CrmCustomerId, cmd.ProductId, cmd.EffectiveDate);

            return Result.Ok(new SubscribeInsurancePolicyResult(
                existing.Id, existing.Status.ToString(), AlreadyExisted: true,
                DebitCommandId: existing.DebitCommandId));
        }

        var subscription = InsuranceSubscription.Create(
            tenantId: tenantId,
            connectionId: row.Connection.Id,
            crmCustomerId: cmd.CrmCustomerId,
            productId: cmd.ProductId,
            effectiveDate: cmd.EffectiveDate,
            premiumAmount: amount,
            currency: currency,
            createdBy: currentUser.Id,
            clock: clock,
            cbsAccountRef: cmd.AccountRef,
            beneficiariesEncrypted: Encrypt(cmd.Beneficiaries));

        db.InsuranceSubscriptions.Add(subscription);

        // ── The evidence, in this same transaction ──────────────────────────
        //
        // The aggregate refuses a proof with no evidence at all, which is the guard that matters:
        // a row claiming consent was collected with nothing to show a controller is worse than no
        // row. The validator refuses it earlier so the caller gets a 400 naming the field.

        // ASS-12's retention, decided at write time. The nightly job would stamp it anyway — it
        // exists for the rows written before the period was configured — but a row born with its
        // own deadline is one no job has to visit, and `RetainUntil` null means "nobody has
        // decided", which is not a state a new row should be in.
        var retainUntil = clock.GetUtcNow().AddYears(retention.Value.ResolvedYears);

        db.ConsentProofs.Add(ConsentProof.Create(
            tenantId: tenantId,
            subscriptionId: subscription.Id,
            crmCustomerId: cmd.CrmCustomerId,
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

        if (cmd.MedicalQuestionnaireCode is { Length: > 0 } code && cmd.MedicalAnswers is { Length: > 0 })
        {
            db.MedicalQuestionnaires.Add(MedicalQuestionnaire.Create(
                tenantId: tenantId,
                subscriptionId: subscription.Id,
                crmCustomerId: cmd.CrmCustomerId,
                questionnaireCode: code,
                answersEncrypted: encryptor.Encrypt(cmd.MedicalAnswers)!,
                capturedBy: currentUser.Id,
                clock: clock,
                requiresMedicalUnderwriting: cmd.RequiresMedicalUnderwriting,
                retainUntil: retainUntil));
        }

        // ── Step one of the chain ───────────────────────────────────────────
        //
        // The facade enlists in this unit of work and deliberately does not save, so the command
        // row, the saga and the evidence commit together. A debit queued without its saga would be
        // money moving with nothing to correlate the outcome to.
        IntegrationCommandId debitCommandId;

        try
        {
            debitCommandId = await integration.CoreBanking.RequestPremiumDebitAsync(
                new PremiumDebitRequest(
                    SubscriptionId: subscription.Id,
                    AccountRef: cmd.AccountRef,
                    Amount: amount,
                    Currency: currency,
                    // Operator-facing and free of personal data: it reaches a core banking system
                    // this platform does not control, and it appears on the customer's statement.
                    Label: $"Prime {row.Product.InsurerProductCode}"),
                ct);
        }
        catch (DomainException ex)
        {
            // The tenant has no active core-banking connection, or the payload was refused.
            // Nothing is saved, so no half-open saga is left behind.
            logger.LogWarning(
                "Subscription for customer {CustomerId} not opened: {Reason}",
                cmd.CrmCustomerId, ex.Message);

            return Result.Fail<SubscribeInsurancePolicyResult>(ex.Message);
        }

        subscription.AttachDebitCommand(debitCommandId.Value, clock);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Subscription {SubscriptionId} opened for customer {CustomerId} at insurer "
            + "{ConnectionId}; premium debit queued as command {CommandId}",
            subscription.Id, cmd.CrmCustomerId, row.Connection.Id, debitCommandId.Value);

        return Result.Ok(new SubscribeInsurancePolicyResult(
            subscription.Id, subscription.Status.ToString(), AlreadyExisted: false,
            DebitCommandId: debitCommandId.Value));
    }

    /// <summary>
    /// Null stays null — an absent value must not become a decryptable blank, the same rule M01's
    /// AML narrative follows.
    /// </summary>
    private string? Encrypt(string? clear)
        => string.IsNullOrWhiteSpace(clear) ? null : encryptor.Encrypt(clear);
}

/// <summary>Error codes of this slice. Stable UPPER_SNAKE, like every other code of the module.</summary>
internal static class InsuranceErrors
{
    public const string ProductNotFound = "INSURANCE_PRODUCT_NOT_FOUND";

    /// <summary>
    /// Suffixed with the reasons, comma-separated, so a caller is told WHY — the catalogue screen
    /// reports the same list.
    /// </summary>
    public const string ProductNotOfferable = "INSURANCE_PRODUCT_NOT_OFFERABLE";

    /// <summary>
    /// Neither the caller nor the catalogue supplied a premium. Refused rather than defaulted to
    /// zero: a subscription with no premium to debit is a policy given away.
    /// </summary>
    public const string PremiumUnknown = "INSURANCE_PREMIUM_UNKNOWN";

    /// <summary>
    /// Neither the caller nor the catalogue named a currency. Refused rather than defaulted: a
    /// platform guess would debit a West-African account in the wrong unit, and no amount is a sum
    /// of money without one.
    /// </summary>
    public const string CurrencyUnknown = "INSURANCE_CURRENCY_UNKNOWN";

    public const string SubscriptionNotFound = "INSURANCE_SUBSCRIPTION_NOT_FOUND";
}
