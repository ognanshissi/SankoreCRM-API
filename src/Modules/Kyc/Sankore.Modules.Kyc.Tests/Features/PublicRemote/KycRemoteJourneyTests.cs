namespace Sankore.Modules.Kyc.Tests.Features.PublicRemote;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// What the customer is asked for, as the PRODUCT defines it (KRM-B-06).
///
/// <para>
/// The page holds none of these rules, so these cases are the specification of the journey: which
/// documents are always required whatever a product says, which depend on the type the customer
/// picked, and which the product adds.
/// </para>
/// </summary>
public sealed class KycRemoteJourneyTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _productLineId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();

    public KycRemoteJourneyTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _settings.GetIntAsync(_tenantId, KycSettingKeys.RemoteMaxQualityRetries,
                Arg.Any<CancellationToken>())
            .Returns(5);

        SetPolicy();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private void SetPolicy(
        bool enabled = true,
        IReadOnlyList<string>? requiredDocuments = null,
        IReadOnlyList<string>? allowedTypes = null)
        => _administration
            .GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(new KycRemotePolicyDto(
                _productLineId, "SANTE-01", "Assurance santé", "Insurance", true,
                enabled, "Manual", "Simplified", 85,
                requiredDocuments ?? [],
                allowedTypes ?? ["Cni", "Passport"],
                72));

    private KycRemoteJourney Journey() => new(_db, _administration, _settings);

    private async Task<KycRemoteInvitation> SeedAsync(Guid? productLineId = null)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.LeadConversion, Guid.NewGuid(), TimeProvider.System,
            productLineId: productLineId ?? _productLineId);

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, file.CustomerId, 72, Guid.Empty, TimeProvider.System);

        _db.KycFiles.Add(file);
        _db.KycRemoteInvitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);
        return invitation;
    }

    private async Task UploadAsync(
        KycRemoteInvitation invitation, KycDocumentKind kind, KycDocumentType? declared = null)
    {
        _db.KycDocuments.Add(KycDocument.Register(
            tenantId: invitation.TenantId,
            kycFileId: invitation.KycFileId,
            kind: kind,
            storageRef: $"ref-{Guid.NewGuid()}",
            contentType: "image/jpeg",
            sizeBytes: 1024,
            sha256: new string('a', 64),
            uploadedBy: invitation.Id,
            clock: TimeProvider.System,
            declaredDocumentType: declared));

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_front_and_the_selfie_are_required_whatever_the_product_says()
    {
        // A product whose RequiredDocuments is empty still collects a document and a face: that is
        // what a remote enrolment IS, and reading them off the product would let a misconfigured
        // line collect nothing at all.
        var invitation = await SeedAsync();

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.RequiredKinds.Should().Contain(KycDocumentKind.IdentityDocumentFront);
        state.RequiredKinds.Should().Contain(KycDocumentKind.Selfie);
        state.Missing.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_product_can_add_a_proof_of_address()
    {
        // CIMA LBC/FT article 13.6 for an insurance product sold at a distance.
        SetPolicy(requiredDocuments: ["ProofOfAddress"]);
        var invitation = await SeedAsync();

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.RequiredKinds.Should().Contain(KycDocumentKind.ProofOfAddress);
    }

    [Fact]
    public async Task Only_the_types_the_product_accepts_are_offered()
    {
        SetPolicy(allowedTypes: ["Cni"]);
        var invitation = await SeedAsync();

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.AllowedDocumentTypes.Should().Equal(KycDocumentType.Cni);
    }

    [Fact]
    public async Task An_unparseable_type_in_the_policy_is_dropped_rather_than_offered()
    {
        // The policy's values are validated on write, so this is a hand-edited row. Offering a
        // type the enum cannot express would produce a button that fails on use.
        SetPolicy(allowedTypes: ["Cni", "DriversLicence"]);
        var invitation = await SeedAsync();

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.AllowedDocumentTypes.Should().Equal(KycDocumentType.Cni);
    }

    // ── The back depends on the TYPE, not on the product ─────────────────────

    [Fact]
    public async Task No_back_is_required_until_the_customer_has_chosen_a_type()
    {
        var invitation = await SeedAsync();

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.RequiredKinds.Should().NotContain(KycDocumentKind.IdentityDocumentBack);
    }

    [Fact]
    public async Task A_card_requires_its_back()
    {
        var invitation = await SeedAsync();
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Cni);

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.ChosenType.Should().Be(KycDocumentType.Cni);
        state.RequiredKinds.Should().Contain(KycDocumentKind.IdentityDocumentBack);
        state.Missing.Should().Contain(KycDocumentKind.IdentityDocumentBack);
    }

    [Fact]
    public async Task A_passport_does_not()
    {
        // Asking for a passport's back produces a photograph of a blank cover, which the OCR then
        // rejects and the customer retries — a loop with no exit.
        var invitation = await SeedAsync();
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Passport);

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.ChosenType.Should().Be(KycDocumentType.Passport);
        state.RequiredKinds.Should().NotContain(KycDocumentKind.IdentityDocumentBack);
    }

    [Fact]
    public async Task Retaking_the_front_with_another_type_changes_what_is_required()
    {
        // How a customer corrects a wrong choice: the newest front wins.
        var invitation = await SeedAsync();
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Cni);
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Passport);

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.ChosenType.Should().Be(KycDocumentType.Passport);
        state.RequiredKinds.Should().NotContain(KycDocumentKind.IdentityDocumentBack);
    }

    // ── Completion ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_passport_journey_is_complete_with_two_photographs()
    {
        var invitation = await SeedAsync();
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Passport);
        await UploadAsync(invitation, KycDocumentKind.Selfie);

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.Missing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_card_journey_needs_three()
    {
        var invitation = await SeedAsync();
        await UploadAsync(invitation, KycDocumentKind.IdentityDocumentFront, KycDocumentType.Cni);
        await UploadAsync(invitation, KycDocumentKind.Selfie);

        var state = await Journey().ReadAsync(invitation, CancellationToken.None);

        state!.Missing.Should().Equal(KycDocumentKind.IdentityDocumentBack);
    }

    // ── A product that stops allowing it mid-journey ─────────────────────────

    [Fact]
    public async Task A_file_with_no_product_walks_the_DEFAULT_journey()
    {
        // Remplace un cas qui épinglait l'inverse. Les deux côtés doivent lire la même règle :
        // l'invitation est maintenant émise sur la politique par défaut, et un parcours qui
        // refuserait de s'ouvrir enverrait le client sur une page morte après lui avoir promis
        // un lien — exactement la panne que ce repli existe pour supprimer.
        var invitation = await SeedAsync(productLineId: Guid.Empty);

        var journey = await Journey().ReadAsync(invitation, CancellationToken.None);

        journey.Should().NotBeNull();

        // Et il demande ce qu'un enrôlement à distance EST, sans rien inventer de plus.
        journey!.AllowedDocumentTypes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_product_that_stops_allowing_remote_collection_ends_the_journey()
    {
        // An administrator disabled it while the customer was uploading. They finish at the branch,
        // and the endpoints say so rather than letting them reach a submission that will be
        // refused.
        SetPolicy(enabled: false);
        var invitation = await SeedAsync();

        (await Journey().ReadAsync(invitation, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_dangling_product_reference_ends_the_journey()
    {
        _administration
            .GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns((KycRemotePolicyDto?)null);

        var invitation = await SeedAsync();

        (await Journey().ReadAsync(invitation, CancellationToken.None)).Should().BeNull();
    }

    // ── Retakes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retakes_are_counted_per_document_and_not_for_the_journey()
    {
        // A customer fighting the glare on a laminated card has not used up the budget for their
        // selfie.
        var invitation = await SeedAsync();

        var tracked = await _db.KycRemoteInvitations
            .AsTracking().FirstAsync(i => i.Id == invitation.Id, CancellationToken.None);

        tracked.RecordQualityRetry(KycDocumentKind.IdentityDocumentFront, TimeProvider.System);
        tracked.RecordQualityRetry(KycDocumentKind.IdentityDocumentFront, TimeProvider.System);
        await _db.SaveChangesAsync(CancellationToken.None);

        var state = await Journey().ReadAsync(tracked, CancellationToken.None);

        state!.RetriesUsed[KycDocumentKind.IdentityDocumentFront.ToString()].Should().Be(2);
        state.RetriesUsed.Should().NotContainKey(KycDocumentKind.Selfie.ToString());
    }

    [Fact]
    public async Task A_hand_edited_retry_column_restarts_the_budget_rather_than_breaking_the_upload()
    {
        // The forgiving direction on purpose: the alternative is a customer whose journey cannot
        // continue because somebody ran an UPDATE.
        //
        // Reached through the private setter because nothing in the domain can produce this state —
        // which is the point: the only way to get here is outside the aggregate, and that is
        // exactly the case the try/catch in QualityRetries() exists for.
        var invitation = await SeedAsync();

        typeof(KycRemoteInvitation)
            .GetProperty(nameof(KycRemoteInvitation.QualityRetriesJson))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(invitation, ["{not json"]);

        invitation.QualityRetries().Should().BeEmpty();

        // And the budget is usable again rather than stuck.
        invitation.RecordQualityRetry(KycDocumentKind.Selfie, TimeProvider.System).Should().Be(1);
    }
}
