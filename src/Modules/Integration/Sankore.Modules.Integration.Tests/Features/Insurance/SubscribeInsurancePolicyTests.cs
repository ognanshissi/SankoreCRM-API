namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;
using KycStatus = Sankore.Modules.Kyc.PublicApi.KycStatus;

/// <summary>
/// ASS-04 on a holding: the subscription of a SIGNED contract is prepared — product, eligibility,
/// payment, consent, the won project's answers — and nothing moves until the holding is sent.
///
/// <para>
/// What matters most: every condition is refused HERE, in front of the agent, and no premium is
/// debited by this step. The debit-then-insurer order is pinned by the chain's own suites.
/// </para>
/// </summary>
public sealed class SubscribeInsurancePolicyTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Opportunity = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Effective = new(2026, 7, 1);
    private const string LineCode = "ASS-OBS";

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly Sankore.Modules.Administration.PublicApi.IAdministrationModule _administration =
        Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>();

    /// <summary>Hand the information sheet to the client before preparing (the default).</summary>
    private bool _deliver = true;

    private HoldingForSync _holding;

    public SubscribeInsurancePolicyTests()
    {
        _holding = Holding();

        _customers.GetHoldingForSyncAsync(Tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => _holding);
        _customers.CanActorAccessClientAsync(Tenant, Customer, Actor, Arg.Any<CancellationToken>())
            .Returns(true);
        _customers.GetInsuranceApplicantFactsAsync(Tenant, Customer, Arg.Any<CancellationToken>())
            .Returns(new InsuranceApplicantFacts(new DateOnly(1980, 3, 1), HasActiveLoan: false));

        _kyc.GetStatusAsync(Tenant, Customer, Arg.Any<CancellationToken>()).Returns(KycStatus.Approved);
        Standing(Standing());
        _kyc.GetLimitsAsync(Tenant, Customer, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Full", false, 0m, 0m, 30, 80));
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private static HoldingForSync Holding(
        string status = "PendingSignature",
        bool signed = true,
        string productCode = LineCode,
        DateOnly? effective = null,
        Guid? opportunity = null,
        int clauseVersion = 1)
        => new(
            HoldingId: Guid.NewGuid(),
            ClientId: Customer,
            ProductLineId: Guid.NewGuid(),
            ProductCode: productCode,
            ProductCategory: "Insurance",
            Status: status,
            ClauseVersion: clauseVersion,
            IsSigned: signed,
            SignedAt: signed ? Now.AddMinutes(-5) : null,
            SignatureReference: signed ? "SIG-1" : null,
            ExternalSystem: null,
            InsuredCapital: 1_000_000m,
            Premium: null,
            Periodicity: "Annual",
            Currency: "XOF",
            EffectiveFrom: effective ?? Effective,
            Designations: [],
            SourceOpportunityId: opportunity);

    private static Sankore.Modules.Administration.PublicApi.InsuranceDistributorStanding Standing(
        bool configured = true, string[]? missing = null, string[]? expired = null, decimal? smig = 60_000m)
        => new(configured, "Broker", "ORIAS-1", "Courtier — art. 540", smig, missing ?? [], expired ?? []);

    private void Standing(Sankore.Modules.Administration.PublicApi.InsuranceDistributorStanding standing)
        => _administration.GetInsuranceDistributorStandingAsync(Tenant, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(standing);

    private Guid SeedCatalogue(
        bool activeProduct = true,
        decimal? fixedPremium = 7_500m,
        string? currency = "XOF",
        ProductPricingMode pricingMode = ProductPricingMode.CatalogueFixed,
        int? minAge = null,
        KycLevel minKycLevel = KycLevel.None,
        bool withCoreBanking = true,
        bool withMandate = true)
    {
        using var db = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        if (withMandate)
        {
            db.Connections.Attach(connection);
            connection.SetMandates(true, "MANDAT-ENC-1", false, null, Actor, new FixedSagaClock(Now));
        }

        if (withCoreBanking)
            InsuranceTestHarness.SeedConnection(
                db, Tenant, IntegrationKind.Fake, name: "CBS", family: IntegrationFamily.CoreBanking);

        var product = InsuranceTestHarness.SeedProduct(
            db, Tenant, connection.Id,
            active: activeProduct, pricingMode: pricingMode,
            fixedPremium: fixedPremium, currency: currency,
            minAge: minAge, minKycLevel: minKycLevel,
            crmProductCode: LineCode);

        db.SaveChanges();
        return product.Id;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(Tenant);
        user.Id.Returns(Actor);
        return user;
    }

    /// <summary>Answers "resolved" so offerability is decided by the insurance product alone.</summary>
    private static CrmProductCatalogue Catalogue()
    {
        var administration = Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>();

        administration.GetProductAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Sankore.Modules.Administration.PublicApi.ProductSummary(
                LineCode, "Assurance obsèques", "Insurance", true, Guid.NewGuid()));

        return new CrmProductCatalogue(
            administration, new FixedTenantContext(Tenant), NullLogger<CrmProductCatalogue>.Instance);
    }

    private async Task<Result<SubscribeInsurancePolicyResult>> PrepareAsync(
        Guid productId,
        PremiumPaymentMode mode = PremiumPaymentMode.CoreBankingDebit,
        decimal? premium = null,
        string? currency = null,
        string? consentEvidence = "scan://consent/1",
        DateOnly? paidOn = null,
        string? commitment = null,
        Guid? payer = null)
    {
        await using var db = _factory.CreateContext();

        if (_deliver && !db.InformationSheetDeliveries.Any(d => d.HoldingId == _holding.HoldingId && d.ProductId == productId))
        {
            var sheet = db.InformationSheets.FirstOrDefault(x => x.ProductId == productId);
            if (sheet is not null)
            {
                db.InformationSheetDeliveries.Add(InformationSheetDelivery.Record(
                    Tenant, _holding.HoldingId, Customer, sheet, InformationSheetChannel.HandedOnPaper,
                    "RECU-1", Actor, new FixedSagaClock(Now.AddMinutes(-10)), null));
                await db.SaveChangesAsync();
            }
        }

        return await new SubscribeInsurancePolicyHandler(
                db, _customers, _kyc, _administration, _leads, Catalogue(),
                Sankore.Modules.Integration.Tests.Features.Commands.CommandsTestHarness.Encryptor(),
                User(),
                Microsoft.Extensions.Options.Options.Create(
                    new Sankore.Modules.Integration.Features.Insurance.Retention.InsuranceRetentionOptions()),
                new FixedSagaClock(Now), NullLogger<SubscribeInsurancePolicyHandler>.Instance)
            .Handle(
                new SubscribeInsurancePolicyCommand(
                    HoldingId: _holding.HoldingId,
                    ProductId: productId,
                    PaymentMode: mode,
                    AccountRef: mode == PremiumPaymentMode.CoreBankingDebit ? "CI0010001-00042" : null,
                    PaymentMethod: mode == PremiumPaymentMode.RecordedByAgent ? RecordedPaymentMethod.MobileMoney : null,
                    PaymentReference: mode == PremiumPaymentMode.RecordedByAgent ? "OM-778899" : null,
                    PaidOn: mode == PremiumPaymentMode.RecordedByAgent ? paidOn ?? new DateOnly(2026, 6, 1) : null,
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
                    CommitmentReference: commitment,
                    CommitmentSignedOn: commitment is null ? null : new DateOnly(2026, 5, 30),
                    PremiumPayerClientId: payer),
                CancellationToken.None);
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_signed_holding_is_prepared_and_NOTHING_is_debited_yet()
    {
        // The chain starts when the holding is sent: preparing it must not move money.
        var productId = SeedCatalogue();

        var result = await PrepareAsync(productId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Status.Should().Be(nameof(SubscriptionStatus.Prepared));
        result.Value.DebitCommandId.Should().BeNull();

        _integration.ReceivedCalls().Should().BeEmpty();

        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();
        saga.CrmHoldingId.Should().Be(_holding.HoldingId);
        saga.CrmCustomerId.Should().Be(Customer, "the client is the holding's, never a caller's");
        saga.EffectiveDate.Should().Be(Effective, "the effective date is the holding's");
        saga.PaymentMode.Should().Be(PremiumPaymentMode.CoreBankingDebit);
        saga.CbsAccountRef.Should().Be("CI0010001-00042");
        saga.PremiumAmount.Should().Be(7_500m);
    }

    [Fact]
    public async Task The_consent_proof_is_written_with_the_subscription()
    {
        var productId = SeedCatalogue();

        var result = await PrepareAsync(productId);

        await using var db = _factory.CreateContext();
        var proof = db.ConsentProofs.Single();

        proof.SubscriptionId.Should().Be(result.Value!.SubscriptionId);
        proof.ConsentTextVersion.Should().Be("CG-2026-03");
        proof.CapturedBy.Should().Be(Actor);
        proof.EvidenceEncrypted.Should().StartWith("v1:");
        proof.EvidenceEncrypted.Should().NotContain("scan://consent/1");
    }

    [Fact]
    public async Task A_premium_collected_by_the_agent_is_recorded_with_its_proof()
    {
        // An institution with no core banking: the premium was paid at the counter.
        var productId = SeedCatalogue(withCoreBanking: false);

        var result = await PrepareAsync(productId, PremiumPaymentMode.RecordedByAgent);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();
        saga.PaymentMode.Should().Be(PremiumPaymentMode.RecordedByAgent);
        saga.RecordedPaymentMethod.Should().Be(RecordedPaymentMethod.MobileMoney);
        saga.RecordedPaymentReference.Should().Be("OM-778899");
        saga.RecordedPaidOn.Should().Be(new DateOnly(2026, 6, 1));
        saga.CbsAccountRef.Should().BeNull();
    }

    // ── the holding ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_holding_outside_the_agents_perimeter_reads_as_not_found()
    {
        var productId = SeedCatalogue();
        _customers.CanActorAccessClientAsync(Tenant, Customer, Actor, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.HoldingNotFound);
    }

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("PendingSignature", false)]
    [InlineData("PendingSync", true)]
    [InlineData("Active", true)]
    public async Task Only_a_signed_holding_awaiting_submission_can_be_prepared(string status, bool signed)
    {
        var productId = SeedCatalogue();
        _holding = Holding(status: status, signed: signed);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.HoldingNotSigned);
    }

    [Fact]
    public async Task The_insurers_product_must_realise_the_holdings_line()
    {
        var productId = SeedCatalogue();
        _holding = Holding(productCode: "EPARGNE-PLUS");

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.ProductNotForHolding);
    }

    // ── eligibility ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_below_the_products_minimum_age_is_refused_with_the_reason()
    {
        var productId = SeedCatalogue(minAge: 50);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.NotEligible}:{InsuranceEligibilityRules.TooYoung}");
    }

    [Fact]
    public async Task A_client_whose_KYC_is_not_approved_has_no_level()
    {
        var productId = SeedCatalogue(minKycLevel: KycLevel.Simplified);
        _kyc.GetStatusAsync(Tenant, Customer, Arg.Any<CancellationToken>()).Returns(KycStatus.InProgress);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.NotEligible}:{InsuranceEligibilityRules.KycInsufficient}");
    }

    [Fact]
    public async Task A_capped_KYC_file_is_the_simplified_tier()
    {
        var productId = SeedCatalogue(minKycLevel: KycLevel.Full);
        _kyc.GetLimitsAsync(Tenant, Customer, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Simplified", true, 500_000m, 1_000_000m, 30, 80));

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.NotEligible}:{InsuranceEligibilityRules.KycInsufficient}");
    }

    // ── payment ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_debit_is_refused_when_the_institution_has_no_core_banking()
    {
        var productId = SeedCatalogue(withCoreBanking: false);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.CoreBankingUnavailable);
    }

    [Fact]
    public async Task A_premium_paid_tomorrow_is_not_a_premium_collected()
    {
        var productId = SeedCatalogue(withCoreBanking: false);

        var result = await PrepareAsync(productId, PremiumPaymentMode.RecordedByAgent, paidOn: new DateOnly(2026, 6, 2));

        result.Error.Should().Be(InsuranceErrors.PaymentDateInFuture);
    }

    [Fact]
    public async Task A_caller_supplied_premium_wins_over_the_catalogue()
    {
        var productId = SeedCatalogue();

        await PrepareAsync(productId, premium: 9_250m);

        await using var db = _factory.CreateContext();
        db.InsuranceSubscriptions.Single().PremiumAmount.Should().Be(9_250m,
            "it is the quote the client was shown and agreed to");
    }

    [Fact]
    public async Task A_product_with_no_premium_anywhere_is_refused()
    {
        var productId = SeedCatalogue(fixedPremium: null, currency: null, pricingMode: ProductPricingMode.InsurerComputed);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.PremiumUnknown);
    }

    [Fact]
    public async Task A_premium_with_no_currency_anywhere_is_refused()
    {
        var productId = SeedCatalogue(fixedPremium: null, currency: null, pricingMode: ProductPricingMode.InsurerComputed);

        var result = await PrepareAsync(productId, premium: 9_250m);

        result.Error.Should().Be(InsuranceErrors.CurrencyUnknown);
    }

    // ── the won project's questionnaires ────────────────────────────────────

    [Fact]
    public async Task Unanswered_questionnaires_on_the_won_project_are_refused()
    {
        var productId = SeedCatalogue();
        _holding = Holding(opportunity: Opportunity);
        _leads.GetProjectAnswersSnapshotAsync(Tenant, Opportunity, Arg.Any<CancellationToken>())
            .Returns(new ProjectAnswersSnapshot(false, 2, true, "[]", "abc"));

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.QuestionnairesIncomplete}:2");
    }

    [Fact]
    public async Task The_answers_as_signed_are_kept_encrypted_with_their_fingerprint()
    {
        var productId = SeedCatalogue();
        _holding = Holding(opportunity: Opportunity);
        const string answers = """[{"subject":"Le titulaire","answers":[{"value":"Non"}]}]""";
        _leads.GetProjectAnswersSnapshotAsync(Tenant, Opportunity, Arg.Any<CancellationToken>())
            .Returns(new ProjectAnswersSnapshot(true, 0, true, answers, "f1ngerprint"));

        var result = await PrepareAsync(productId);

        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();
        saga.AnswersSha256.Should().Be("f1ngerprint");
        saga.SourceOpportunityId.Should().Be(Opportunity);

        var questionnaire = db.MedicalQuestionnaires.Single();
        questionnaire.SubscriptionId.Should().Be(result.Value!.SubscriptionId);
        questionnaire.QuestionnaireCode.Should().Be(SubscribeInsurancePolicyHandler.ProjectQuestionnaireCode);
        questionnaire.AnswersEncrypted.Should().NotContain("Le titulaire");
    }

    // ── idempotence ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Preparing_again_before_sending_replaces_the_previous_preparation()
    {
        var productId = SeedCatalogue();

        var first = await PrepareAsync(productId);
        var second = await PrepareAsync(productId, premium: 8_000m);

        second.Value!.SubscriptionId.Should().NotBe(first.Value!.SubscriptionId);

        await using var db = _factory.CreateContext();
        db.InsuranceSubscriptions.Single(s => s.Id == first.Value.SubscriptionId)
            .Status.Should().Be(SubscriptionStatus.Cancelled, "its proof stays what it was");
        db.InsuranceSubscriptions.Single(s => s.Id == second.Value.SubscriptionId)
            .PremiumAmount.Should().Be(8_000m);
    }

    [Fact]
    public async Task Once_sent_the_subscription_is_reported_rather_than_prepared_twice()
    {
        var productId = SeedCatalogue();
        var first = await PrepareAsync(productId);

        await using (var db = _factory.CreateContext())
        {
            var saga = db.InsuranceSubscriptions.AsTracking().Single();
            saga.StartWithDebit(Guid.NewGuid(), new FixedSagaClock(Now));
            await db.SaveChangesAsync();
        }

        var again = await PrepareAsync(productId);

        again.Value!.AlreadyExisted.Should().BeTrue();
        again.Value.SubscriptionId.Should().Be(first.Value!.SubscriptionId);
    }

    // ── lot 2: the distributor, the sheet, the mandate, the derogation ─────

    [Fact]
    public async Task An_institution_whose_RC_pro_expired_cannot_subscribe()
    {
        var productId = SeedCatalogue();
        Standing(Standing(expired: ["ProfessionalLiability"]));

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.DistributorNotInGoodStanding}:EXPIRED_ProfessionalLiability");
    }

    [Fact]
    public async Task An_institution_that_never_filled_its_distributor_profile_cannot_subscribe()
    {
        var productId = SeedCatalogue();
        Standing(Sankore.Modules.Administration.PublicApi.InsuranceDistributorStanding.NotConfigured);

        var result = await PrepareAsync(productId);

        result.Error.Should().Be($"{InsuranceErrors.DistributorNotInGoodStanding}:NOT_CONFIGURED");
    }

    [Fact]
    public async Task No_subscription_without_the_information_sheet_handed_over()
    {
        var productId = SeedCatalogue();
        _deliver = false;

        var result = await PrepareAsync(productId);

        result.Error.Should().Be(InsuranceErrors.InformationSheetNotDelivered);
    }

    [Fact]
    public async Task A_product_with_no_information_sheet_is_not_offerable()
    {
        Guid productId;
        await using (var db = _factory.CreateContext())
        {
            var connection = InsuranceTestHarness.SeedConnection(db, Tenant, IntegrationKind.Fake, name: "Assureur");
            productId = InsuranceTestHarness.SeedProduct(
                db, Tenant, connection.Id, crmProductCode: LineCode, withInformationSheet: false).Id;
        }

        var result = await PrepareAsync(productId);

        result.Error.Should().Contain(ProductOfferability.InformationSheetMissing);
    }

    [Theory]
    [InlineData(PremiumPaymentMode.CoreBankingDebit)]
    [InlineData(PremiumPaymentMode.RecordedByAgent)]
    public async Task Without_a_collection_mandate_the_distributor_cannot_collect(PremiumPaymentMode mode)
    {
        var productId = SeedCatalogue(withMandate: false);

        var result = await PrepareAsync(productId, mode);

        result.Error.Should().Be(InsuranceErrors.NoCollectionMandate);
    }

    [Fact]
    public async Task Without_a_mandate_the_insurer_collects_and_nothing_is_collected_here()
    {
        var productId = SeedCatalogue(withMandate: false, withCoreBanking: false);

        var result = await PrepareAsync(productId, PremiumPaymentMode.InsurerCollects);

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();
        saga.PaymentMode.Should().Be(PremiumPaymentMode.InsurerCollects);
        saga.InformationSheetDeliveryId.Should().NotBeNull("the subscription names the sheet the client received");
    }

    [Fact]
    public async Task The_derogation_is_for_premiums_above_80_times_the_SMIG()
    {
        // 80 × 60 000 = 4 800 000: a 7 500 premium does not qualify.
        var productId = SeedCatalogue();

        var result = await PrepareAsync(productId, PremiumPaymentMode.DeferredByDerogation, commitment: "ENG-1");

        result.Error.Should().Be(InsuranceErrors.DerogationNotAllowed);
    }

    [Fact]
    public async Task A_large_premium_with_the_written_commitment_may_be_paid_within_60_days()
    {
        var productId = SeedCatalogue();

        var result = await PrepareAsync(productId, PremiumPaymentMode.DeferredByDerogation, premium: 5_000_000m, commitment: "ENG-1");

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var db = _factory.CreateContext();
        var saga = db.InsuranceSubscriptions.Single();
        saga.CommitmentReference.Should().Be("ENG-1");
        saga.IsPremiumOutstanding.Should().BeTrue();
    }

    // ── lot 4: a third-party payer ──────────────────────────────────────────

    [Fact]
    public async Task A_payer_outside_the_agents_perimeter_reads_as_not_found()
    {
        // Otherwise « KYC not approved » would tell that a client of another branch exists.
        var productId = SeedCatalogue();
        var payer = Guid.NewGuid();
        _customers.GetClientSummaryAsync(Tenant, payer, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(payer, "CL-2", "Individual", "Payeur", "Active", Guid.NewGuid(), null, "Approved", "Low", null));

        var result = await PrepareAsync(productId, payer: payer);

        result.Error.Should().Be(InsuranceErrors.PayerNotFound);
        await _kyc.DidNotReceive().GetStatusAsync(Tenant, payer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_payer_whose_KYC_is_not_approved_is_refused()
    {
        var productId = SeedCatalogue();
        var payer = Guid.NewGuid();
        _customers.GetClientSummaryAsync(Tenant, payer, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(payer, "CL-2", "Individual", "Payeur", "Active", Guid.NewGuid(), null, "InProgress", "Low", null));
        _customers.CanActorAccessClientAsync(Tenant, payer, Actor, Arg.Any<CancellationToken>()).Returns(true);
        _kyc.GetStatusAsync(Tenant, payer, Arg.Any<CancellationToken>()).Returns(KycStatus.InProgress);

        var result = await PrepareAsync(productId, payer: payer);

        result.Error.Should().Be(InsuranceErrors.PayerKycNotApproved);
    }

    // ── the catalogue ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_product_is_refused()
    {
        SeedCatalogue();

        var result = await PrepareAsync(Guid.NewGuid());

        result.Error.Should().Be(InsuranceErrors.ProductNotFound);
    }

    [Fact]
    public async Task A_withdrawn_product_is_refused_with_its_reasons()
    {
        var productId = SeedCatalogue(activeProduct: false);

        var result = await PrepareAsync(productId);

        result.Error.Should().StartWith(InsuranceErrors.ProductNotOfferable);
    }

    // ── the request's shape ─────────────────────────────────────────────────

    [Fact]
    public void The_validator_asks_for_the_half_of_the_payment_the_mode_needs()
    {
        var validator = new SubscribeInsurancePolicyValidator();

        validator.Validate(Command(PremiumPaymentMode.CoreBankingDebit, accountRef: null))
            .IsValid.Should().BeFalse();
        validator.Validate(Command(PremiumPaymentMode.RecordedByAgent, accountRef: null))
            .IsValid.Should().BeFalse("method, reference and date are missing");
        validator.Validate(Command(PremiumPaymentMode.RecordedByAgent, accountRef: null,
                method: RecordedPaymentMethod.Cash, reference: "REC-1", paidOn: new DateOnly(2026, 6, 1)))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_names_the_missing_evidence_field()
    {
        var validator = new SubscribeInsurancePolicyValidator();

        var missing = validator.Validate(Command(evidence: null));
        missing.IsValid.Should().BeFalse();
        missing.Errors.Should().Contain(e => e.ErrorMessage.Contains("consentEvidence"));
    }

    [Fact]
    public void The_validator_refuses_a_consent_dated_in_the_future()
    {
        var validator = new SubscribeInsurancePolicyValidator();

        validator.Validate(Command(consentedAt: DateTimeOffset.UtcNow.AddHours(2))).IsValid.Should().BeFalse();
        validator.Validate(Command(consentedAt: DateTimeOffset.UtcNow.AddMinutes(2))).IsValid.Should().BeTrue();
    }

    private static SubscribeInsurancePolicyCommand Command(
        PremiumPaymentMode mode = PremiumPaymentMode.CoreBankingDebit,
        string? accountRef = "CI0010001-00042",
        RecordedPaymentMethod? method = null,
        string? reference = null,
        DateOnly? paidOn = null,
        string? evidence = "scan://consent/1",
        DateTimeOffset? consentedAt = null)
        => new(
            HoldingId: Guid.NewGuid(),
            ProductId: Guid.NewGuid(),
            PaymentMode: mode,
            AccountRef: accountRef,
            PaymentMethod: method,
            PaymentReference: reference,
            PaidOn: paidOn,
            PremiumAmount: 7_500m,
            Currency: "XOF",
            ConsentChannel: ConsentChannel.OneTimeCode,
            ConsentTextVersion: "CG-2026-03",
            ConsentedAt: consentedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ConsentTextSha256: null,
            ConsentEvidence: evidence,
            ConsentEvidenceStorageRef: null,
            ConsentEvidenceSha256: null,
            ConsentEvidenceContentType: null);

    private sealed class FixedSagaClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
