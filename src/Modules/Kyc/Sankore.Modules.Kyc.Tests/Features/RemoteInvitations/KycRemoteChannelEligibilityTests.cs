namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// Quel canal est ouvert, et pourquoi l'autre ne l'est pas.
///
/// <para>
/// Ce que ces cas épinglent avant tout, c'est l'<b>indépendance</b> des deux réponses : une
/// fiche sans adresse e-mail ne doit pas fermer le SMS, et un établissement sans fournisseur SMS
/// ne doit pas fermer l'e-mail. Une seule réponse pour les deux canaux était l'état antérieur, et
/// c'est précisément ce qui rendait un client joignable injoignable.
/// </para>
/// </summary>
public sealed class KycRemoteChannelEligibilityTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _productLineId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public KycRemoteChannelEligibilityTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _administration
            .GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(new KycRemotePolicyDto(
                _productLineId, "SANTE-01", "Assurance santé", "Insurance", true,
                true, "Manual", "Simplified", 85, [], ["Cni"], LinkValidityHours: 72));

        // La situation nominale : le client a les deux, et l'établissement sait envoyer.
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(true);
        _customers.HasPhoneAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(true);
        SmsChannel(canSend: true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Both_channels_are_open_when_nothing_is_missing()
    {
        var verdict = await EvaluateAsync();

        verdict.CanIssueByEmail.Should().BeTrue();
        verdict.CanIssueBySms.Should().BeTrue();
        verdict.Reason.Should().BeNull();
        verdict.SmsReason.Should().BeNull();
    }

    [Fact]
    public async Task A_customer_with_no_email_can_still_be_TEXTED()
    {
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns(false);

        var verdict = await EvaluateAsync();

        verdict.CanIssueByEmail.Should().BeFalse();
        verdict.Reason.Should().Be(KycErrors.RemoteInvitationNoEmail);

        // Le cas qui motive tout ce lot : un prospect saisi sur le terrain avec un téléphone et
        // rien d'autre était refusé d'un bloc.
        verdict.CanIssueBySms.Should().BeTrue();
        verdict.CanIssue.Should().BeTrue();
    }

    [Fact]
    public async Task A_customer_with_no_phone_can_still_be_MAILED()
    {
        _customers.HasPhoneAsync(_tenantId, _customerId, Arg.Any<CancellationToken>())
            .Returns(false);

        var verdict = await EvaluateAsync();

        verdict.CanIssueBySms.Should().BeFalse();
        verdict.SmsReason.Should().Be(KycErrors.RemoteInvitationNoPhone);
        verdict.CanIssueByEmail.Should().BeTrue();
    }

    [Fact]
    public async Task An_institution_with_no_SMS_channel_is_named_as_the_cause_and_not_the_customer()
    {
        _customers.HasPhoneAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(true);
        SmsChannel(canSend: false);

        var verdict = await EvaluateAsync();

        // La distinction est tout l'intérêt des deux codes : dire « pas de téléphone » enverrait
        // l'agent corriger une fiche parfaitement complète, pour un fournisseur que son
        // administrateur n'a jamais configuré.
        verdict.SmsReason.Should().Be(KycErrors.RemoteInvitationSmsUnavailable);
        verdict.CanIssueByEmail.Should().BeTrue();
    }

    [Fact]
    public async Task An_OUTAGE_closes_the_SMS_channel_rather_than_opening_it()
    {
        _administration.GetSmsConfigAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns<TenantSmsConfigDto?>(_ => throw new InvalidOperationException("M12 down"));

        var verdict = await EvaluateAsync();

        verdict.CanIssueBySms.Should().BeFalse();
        verdict.SmsReason.Should().Be(KycErrors.RemoteInvitationSmsUnavailable);
    }

    [Fact]
    public async Task A_file_that_is_no_longer_collecting_closes_BOTH_channels()
    {
        var verdict = await EvaluateAsync(status: KycFileStatus.Verifying);

        verdict.CanIssue.Should().BeFalse();
        verdict.ReasonFor(KycRemoteInvitationChannel.Email)
            .Should().Be(KycErrors.RemoteInvitationFileNotCollecting);
        verdict.ReasonFor(KycRemoteInvitationChannel.Sms)
            .Should().Be(KycErrors.RemoteInvitationFileNotCollecting);
    }

    [Fact]
    public async Task A_customer_reachable_by_neither_is_refused_outright()
    {
        _customers.HasEmailAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(false);
        _customers.HasPhoneAsync(_tenantId, _customerId, Arg.Any<CancellationToken>()).Returns(false);

        var verdict = await EvaluateAsync();

        verdict.CanIssue.Should().BeFalse();
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private void SmsChannel(bool canSend)
        => _administration.GetSmsConfigAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto(
                ProviderType: canSend ? "Brevo" : "Disabled",
                SenderId: canSend ? "SANKORE" : null,
                AccountSid: null,
                DefaultCallingCode: canSend ? "225" : null,
                HasCredential: canSend,
                CanSend: canSend,
                MonthlyQuotaLimit: null,
                CurrentMonthUsageCount: 0));

    private async Task<KycRemoteEligibility.Verdict> EvaluateAsync(
        KycFileStatus status = KycFileStatus.Collecting,
        bool withProductLine = true)
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            agencyId: Guid.NewGuid(),
            // Null reproduit ce qu'ouvrent `POST kyc-files` et ClientCreatedKycConsumer.
            productLineId: withProductLine ? _productLineId : null,
            responsibleAgentId: Guid.NewGuid());

        // Soumis pour vérification : le dossier quitte la collecte, ce qui est le seul état
        // dont ce cas a besoin — n'importe quel statut hors collecte ferme les deux canaux.
        if (status != KycFileStatus.Collecting)
            file.SubmitForVerification(Guid.NewGuid(), TimeProvider.System);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);

        var eligibility = new KycRemoteEligibility(_db, _administration, _customers);

        return await eligibility.EvaluateAsync(_tenantId, file.Id, CancellationToken.None);
    }

    // ── Un dossier sans ligne produit ──────────────────────────────────────

    [Fact]
    public async Task A_file_with_NO_product_line_can_still_be_collected_remotely()
    {
        // Le cas signalé : deux des trois façons d'ouvrir un dossier n'attachent aucun produit,
        // et l'écran KYC est l'une d'elles. Le refus parlait « du produit souscrit » à propos
        // d'un produit inexistant, et fermait la collecte à distance à tous ces dossiers.
        var verdict = await EvaluateAsync(withProductLine: false);

        verdict.CanIssue.Should().BeTrue();
        verdict.Reason.Should().BeNull();
        verdict.SmsReason.Should().BeNull();
    }

    [Fact]
    public async Task The_fallback_policy_NEVER_allows_an_automatic_approval()
    {
        var verdict = await EvaluateAsync(withProductLine: false);

        // Lu par KycAutoApprovalPolicy comme « pas automatique » : aucune règle n'a été choisie
        // pour ce dossier, donc la machine n'a rien à décider à la place de personne.
        verdict.Policy!.ValidationMode.Should().NotBe("Automatic");
        verdict.Policy.RequiredTier.Should().Be("Simplified");
        verdict.Policy.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_fallback_policy_offers_every_document_type()
    {
        var verdict = await EvaluateAsync(withProductLine: false);

        // Une liste vide laisserait le parcours sans type à proposer, ce qui se lit comme une
        // page cassée et non comme une règle.
        verdict.Policy!.AllowedDocumentTypes.Should().NotBeEmpty();

        // Et rien d'inventé au-delà de ce qu'un enrôlement à distance EST : la liste des pièces
        // exigées s'ajoute au recto et au selfie que le parcours impose déjà de lui-même.
        verdict.Policy.RequiredDocuments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_WITH_a_product_that_forbids_remote_collection_is_still_refused()
    {
        // Le repli ne doit pas avaler la vraie règle : un produit qui refuse la collecte à
        // distance la refuse toujours, et c'est tout l'intérêt de la politique par produit.
        _administration
            .GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(new KycRemotePolicyDto(
                _productLineId, "SANTE-01", "Assurance santé", "Insurance", true,
                false, "Manual", "Simplified", 85, [], ["Cni"], LinkValidityHours: 72));

        var verdict = await EvaluateAsync();

        verdict.CanIssue.Should().BeFalse();
        verdict.Reason.Should().Be(KycErrors.RemoteInvitationProductNotEligible);
    }

    [Fact]
    public async Task A_file_with_no_product_is_still_refused_when_it_has_left_collection()
    {
        var verdict = await EvaluateAsync(status: KycFileStatus.Verifying, withProductLine: false);

        verdict.CanIssue.Should().BeFalse();
        verdict.Reason.Should().Be(KycErrors.RemoteInvitationFileNotCollecting);
    }
}
