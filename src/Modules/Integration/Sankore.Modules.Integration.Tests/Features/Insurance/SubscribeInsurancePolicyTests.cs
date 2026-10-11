namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// ASS-04 and the opening of ASS-05's chain.
///
/// <para>
/// What matters most here is the ORDER: the premium is debited before the insurer is asked. A
/// premium collected against a policy the insurer then refuses can be put right by a reversal; a
/// policy issued against a premium that was never collected is an insurer's receivable against a
/// customer nobody debited. Several cases below exist only to pin that direction.
/// </para>
///
/// <para>
/// And the consent proof, which the 2024 CIMA regulation on distribution by electronic means
/// requires as a timestamped record of each step: it is written in the SAME transaction as the
/// subscription, for the reason BNF-B-18 already paid for on a health declaration — evidence
/// recorded by a second request can fail on its own.
/// </para>
/// </summary>
public sealed class SubscribeInsurancePolicyTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Effective = new(2026, 7, 1);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly ICoreBankingGateway _cbs = Substitute.For<ICoreBankingGateway>();

    private readonly List<PremiumDebitRequest> _debits = [];
    private Guid _debitCommandId = Guid.NewGuid();

    public SubscribeInsurancePolicyTests()
    {
        _integration.CoreBanking.Returns(_cbs);

        _cbs.RequestPremiumDebitAsync(
                Arg.Do<PremiumDebitRequest>(_debits.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_debitCommandId));
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <param name="pricingMode">
    /// <c>CatalogueFixed</c> cannot hold a null premium or a null currency — <c>InsuranceProduct</c>
    /// refuses to be created that way, which is why the handler's PremiumUnknown and
    /// CurrencyUnknown guards are reachable only for an <c>InsurerComputed</c> entry: that is the
    /// mode where the catalogue legitimately holds neither and a quote names them.
    /// </param>
    private (Guid ConnectionId, Guid ProductId) SeedCatalogue(
        bool activeProduct = true,
        decimal? fixedPremium = 7_500m,
        string? currency = "XOF",
        ProductPricingMode pricingMode = ProductPricingMode.CatalogueFixed)
    {
        using var db = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(
            db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var product = InsuranceTestHarness.SeedProduct(
            db, Tenant, connection.Id,
            active: activeProduct, pricingMode: pricingMode,
            fixedPremium: fixedPremium, currency: currency);

        db.SaveChanges();
        return (connection.Id, product.Id);
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(Tenant);
        user.Id.Returns(Actor);
        return user;
    }

    /// <summary>
    /// The CRM catalogue probe. Answers "resolved" so offerability is decided by the insurance
    /// product alone — the M12 half has its own suite.
    /// </summary>
    private static CrmProductCatalogue Catalogue()
    {
        var administration = Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>();

        administration.GetProductAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Sankore.Modules.Administration.PublicApi.ProductSummary(
                "ASS-VIE-EMP", "Assurance emprunteur", "Insurance", true, Guid.NewGuid()));

        return new CrmProductCatalogue(
            administration,
            new FixedTenantContext(Tenant),
            NullLogger<CrmProductCatalogue>.Instance);
    }

    private async Task<Result<SubscribeInsurancePolicyResult>> SubscribeAsync(
        Guid productId,
        decimal? premium = null,
        string? currency = null,
        string? consentEvidence = "scan://consent/1",
        string? medicalCode = null,
        string? medicalAnswers = null,
        string? beneficiaries = null,
        DateOnly? effective = null)
    {
        await using var db = _factory.CreateContext();

        return await new SubscribeInsurancePolicyHandler(
                db, _integration, Catalogue(), Sankore.Modules.Integration.Tests.Features.Commands.CommandsTestHarness.Encryptor(), User(),
                new FixedSagaClock(Now), NullLogger<SubscribeInsurancePolicyHandler>.Instance)
            .Handle(
                new SubscribeInsurancePolicyCommand(
                    ProductId: productId,
                    CrmCustomerId: Customer,
                    EffectiveDate: effective ?? Effective,
                    AccountRef: "CI0010001-00042",
                    PremiumAmount: premium,
                    Currency: currency,
                    ConsentChannel: ConsentChannel.OneTimeCode,
                    ConsentTextVersion: "CG-2026-03",
                    ConsentedAt: Now.AddMinutes(-2),
                    ConsentTextSha256: null,
                    ConsentEvidence: consentEvidence,
                    ConsentEvidenceStorageRef: null,
                    ConsentEvidenceSha256: null,
                    ConsentEvidenceContentType: null,
                    MedicalQuestionnaireCode: medicalCode,
                    MedicalAnswers: medicalAnswers,
                    RequiresMedicalUnderwriting: false,
                    Beneficiaries: beneficiaries),
                CancellationToken.None);
    }

    // ── the happy path, and the order ───────────────────────────────────────

    [Fact]
    public async Task A_subscription_debits_the_premium_BEFORE_asking_the_insurer()
    {
        // The whole design of ASS-05 in one assertion: at the end of the opening step the debit
        // is queued and the insurer has not been asked at all.
        var (_, productId) = SeedCatalogue();

        var result = await SubscribeAsync(productId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(nameof(SubscriptionStatus.PendingDebit));
        result.Value.DebitCommandId.Should().Be(_debitCommandId);

        _debits.Should().ContainSingle()
            .Which.Amount.Should().Be(7_500m);

        await _integration.Insurance.DidNotReceiveWithAnyArgs()
            .RequestPolicySubscriptionAsync(default!, default);
    }

    [Fact]
    public async Task The_debit_is_keyed_on_the_subscription_so_a_retry_cannot_debit_twice()
    {
        var (_, productId) = SeedCatalogue();

        var first = await SubscribeAsync(productId);

        _debits.Should().ContainSingle()
            .Which.SubscriptionId.Should().Be(first.Value!.SubscriptionId);

        // The second request is the same customer, product and effective date.
        var second = await SubscribeAsync(productId);

        second.IsSuccess.Should().BeTrue("the caller asked for a subscription and there is one");
        second.Value!.AlreadyExisted.Should().BeTrue();
        second.Value.SubscriptionId.Should().Be(first.Value.SubscriptionId);

        // And no second debit was queued, which is the point.
        _debits.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_different_effective_date_is_a_different_subscription()
    {
        // The idempotency is on the triple, not on the customer: a renewal for next year is a
        // genuine second subscription and must not attach to this one.
        var (_, productId) = SeedCatalogue();

        var first = await SubscribeAsync(productId);
        var next = await SubscribeAsync(productId, effective: Effective.AddYears(1));

        next.Value!.AlreadyExisted.Should().BeFalse();
        next.Value.SubscriptionId.Should().NotBe(first.Value!.SubscriptionId);
        _debits.Should().HaveCount(2);
    }

    // ── the evidence, in the same transaction ───────────────────────────────

    [Fact]
    public async Task The_consent_proof_is_written_with_the_subscription()
    {
        var (_, productId) = SeedCatalogue();

        var result = await SubscribeAsync(productId);

        await using var db = _factory.CreateContext();
        var proof = db.ConsentProofs.Single();

        proof.SubscriptionId.Should().Be(result.Value!.SubscriptionId);
        proof.ConsentTextVersion.Should().Be("CG-2026-03",
            "a proof that does not name the notice does not say what was consented to");
        proof.Channel.Should().Be(ConsentChannel.OneTimeCode);
        proof.ConsentedAt.Should().Be(Now.AddMinutes(-2));
        proof.CapturedBy.Should().Be(Actor);

        // REAL AES-256-GCM in this project, not a passthrough: the payload is
        // v1:nonce:tag:ciphertext, so what is asserted is that the clear value is nowhere in it.
        proof.EvidenceEncrypted.Should().StartWith("v1:");
        proof.EvidenceEncrypted.Should().NotContain("scan://consent/1");
    }

    [Fact]
    public async Task A_subscription_with_no_consent_evidence_is_refused_before_anything_is_written()
    {
        // The aggregate refuses it, so nothing is saved — and critically no debit is queued: the
        // money must not move for a subscription the platform cannot evidence.
        var (_, productId) = SeedCatalogue();

        var act = async () => await SubscribeAsync(productId, consentEvidence: null);

        await act.Should().ThrowAsync<DomainException>();

        await using var db = _factory.CreateContext();
        db.InsuranceSubscriptions.Should().BeEmpty();
        db.ConsentProofs.Should().BeEmpty();
        _debits.Should().BeEmpty();
    }

    [Fact]
    public void The_validator_names_the_missing_evidence_field()
    {
        // So a caller gets a 400 that says which field, rather than the aggregate's 500.
        var validator = new SubscribeInsurancePolicyValidator();

        var missing = validator.Validate(Command(evidence: null, storageRef: null));
        missing.IsValid.Should().BeFalse();
        missing.Errors.Should().Contain(e => e.ErrorMessage.Contains("consentEvidence"));

        validator.Validate(Command(evidence: null, storageRef: "obj://consent/1"))
            .IsValid.Should().BeTrue("a stored reference is evidence too");
    }

    [Fact]
    public void The_validator_refuses_a_consent_dated_in_the_future()
    {
        var validator = new SubscribeInsurancePolicyValidator();

        validator.Validate(Command(consentedAt: DateTimeOffset.UtcNow.AddHours(2)))
            .IsValid.Should().BeFalse();

        // Clock skew on an agent's own device is tolerated, which is why the bound is not exact.
        validator.Validate(Command(consentedAt: DateTimeOffset.UtcNow.AddMinutes(2)))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_refuses_half_a_medical_questionnaire()
    {
        // A code with no answers asserts a questionnaire was collected while holding nothing;
        // answers with no code cannot be read back against the questions that were asked.
        var validator = new SubscribeInsurancePolicyValidator();

        validator.Validate(Command(medicalCode: "MED-01")).IsValid.Should().BeFalse();
        validator.Validate(Command(medicalAnswers: "{}")).IsValid.Should().BeFalse();
        validator.Validate(Command(medicalCode: "MED-01", medicalAnswers: "{}"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_medical_answers_are_encrypted_and_only_stored_when_both_halves_are_given()
    {
        var (_, productId) = SeedCatalogue();

        await SubscribeAsync(productId, medicalCode: "MED-01", medicalAnswers: "{\"fumeur\":true}");

        await using var db = _factory.CreateContext();
        var questionnaire = db.MedicalQuestionnaires.Single();

        questionnaire.QuestionnaireCode.Should().Be("MED-01");
        questionnaire.AnswersEncrypted.Should().StartWith("v1:");
        questionnaire.AnswersEncrypted.Should().NotContain("fumeur");
    }

    [Fact]
    public async Task No_questionnaire_row_is_written_when_none_was_collected()
    {
        var (_, productId) = SeedCatalogue();

        await SubscribeAsync(productId);

        await using var db = _factory.CreateContext();
        db.MedicalQuestionnaires.Should().BeEmpty();
    }

    [Fact]
    public async Task The_beneficiaries_are_encrypted_on_the_saga()
    {
        // Names, relationships and dates of birth of people who are not the customer — PII about
        // third parties the platform has no other record of.
        var (_, productId) = SeedCatalogue();

        await SubscribeAsync(productId, beneficiaries: "Awa Ouattara;Kofi Ouattara");

        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();

        saga.BeneficiariesEncrypted.Should().StartWith("v1:");
        saga.BeneficiariesEncrypted.Should().NotContain("Ouattara");
    }

    // ── what refuses a subscription ─────────────────────────────────────────

    [Fact]
    public async Task An_unknown_product_is_refused()
    {
        SeedCatalogue();

        var result = await SubscribeAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceErrors.ProductNotFound);
    }

    [Fact]
    public async Task A_withdrawn_product_is_refused_with_its_reasons()
    {
        // Through the SAME offerability helper the catalogue screen reports with: a product the
        // screen shows as unofferable must not be subscribable.
        var (_, productId) = SeedCatalogue(activeProduct: false);

        var result = await SubscribeAsync(productId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith(InsuranceErrors.ProductNotOfferable);
        result.Error.Should().Contain(ProductOfferability.ProductInactive);

        _debits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_product_with_no_premium_anywhere_is_refused_rather_than_debited_for_zero()
    {
        // An insurer-priced entry holds no premium of its own: the quote names it. A caller who
        // subscribes without quoting first has nothing to debit, and zero is not the answer.
        var (_, productId) = SeedCatalogue(
            fixedPremium: null, currency: "XOF", pricingMode: ProductPricingMode.InsurerComputed);

        var result = await SubscribeAsync(productId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceErrors.PremiumUnknown);
        _debits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_supplied_premium_wins_over_the_catalogue()
    {
        // It is the quote the customer was shown and agreed to. Re-deriving it here could debit a
        // figure nobody signed.
        var (_, productId) = SeedCatalogue(fixedPremium: 7_500m);

        await SubscribeAsync(productId, premium: 9_250m);

        _debits.Should().ContainSingle().Which.Amount.Should().Be(9_250m);

        await using var db = _factory.CreateContext();
        db.InsuranceSubscriptions.Single().PremiumAmount.Should().Be(9_250m);
    }

    [Fact]
    public async Task A_premium_with_no_currency_anywhere_is_refused()
    {
        // The product's currency is nullable on an insurer-priced entry, so a platform guess
        // would debit a West-African account in the wrong unit.
        var (_, productId) = SeedCatalogue(
            fixedPremium: null, currency: null, pricingMode: ProductPricingMode.InsurerComputed);

        var result = await SubscribeAsync(productId, premium: 9_250m);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceErrors.CurrencyUnknown);
        _debits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_core_banking_refusal_leaves_no_half_open_saga_behind()
    {
        // The facade enlists in this unit of work and does not save, so nothing is committed when
        // the enqueue throws — the tenant has no active CBS connection, say.
        var (_, productId) = SeedCatalogue();

        _cbs.RequestPremiumDebitAsync(
                Arg.Any<PremiumDebitRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException(
                $"{IntegrationErrors.NoActiveConnection}: this tenant has no core banking connection."));

        var result = await SubscribeAsync(productId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(IntegrationErrors.NoActiveConnection);

        await using var db = _factory.CreateContext();
        db.InsuranceSubscriptions.Should().BeEmpty();
        db.ConsentProofs.Should().BeEmpty("a consent proof without its subscription proves nothing");
    }

    [Fact]
    public async Task Another_tenants_product_cannot_be_subscribed()
    {
        var otherTenant = Guid.NewGuid();

        await using (var other = _factory.ContextFor(otherTenant))
        {
            var connection = InsuranceTestHarness.SeedConnection(
                other, otherTenant, IntegrationKind.Fake, name: "Autre assureur");

            InsuranceTestHarness.SeedProduct(other, otherTenant, connection.Id);
            await other.SaveChangesAsync();
        }

        Guid foreignProductId;
        await using (var other = _factory.ContextFor(otherTenant))
        {
            foreignProductId = other.InsuranceProducts.Select(p => p.Id).Single();
        }

        var result = await SubscribeAsync(foreignProductId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(InsuranceErrors.ProductNotFound,
            "the query filter makes another tenant's product simply absent");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SubscribeInsurancePolicyCommand Command(
        string? evidence = "scan://consent/1",
        string? storageRef = null,
        DateTimeOffset? consentedAt = null,
        string? medicalCode = null,
        string? medicalAnswers = null)
        => new(
            ProductId: Guid.NewGuid(),
            CrmCustomerId: Customer,
            EffectiveDate: Effective,
            AccountRef: "CI0010001-00042",
            PremiumAmount: 7_500m,
            Currency: "XOF",
            ConsentChannel: ConsentChannel.OneTimeCode,
            ConsentTextVersion: "CG-2026-03",
            ConsentedAt: consentedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ConsentTextSha256: null,
            ConsentEvidence: evidence,
            ConsentEvidenceStorageRef: storageRef,
            ConsentEvidenceSha256: null,
            ConsentEvidenceContentType: null,
            MedicalQuestionnaireCode: medicalCode,
            MedicalAnswers: medicalAnswers,
            RequiresMedicalUnderwriting: false,
            Beneficiaries: null);

    private sealed class FixedSagaClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
