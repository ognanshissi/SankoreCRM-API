namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Documents.ListDocuments;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The list the module never had — and the one place that has to be honest about documents
/// collected before per-document review existed.
///
/// <para>
/// There is no backfill, deliberately: an older image left only a storage ref on the verification
/// evidence, and its content type, size and plaintext digest were computed inside the store and
/// persisted nowhere, so a migration could neither recover nor recompute them. They are projected
/// here instead, at <c>NotReviewed</c> with a null id — evidence of what was collected, not work
/// waiting for a validator.
/// </para>
/// </summary>
public sealed class ListKycDocumentsHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _validatorId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ListKycDocumentsHandler _handler;

    public ListKycDocumentsHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _handler = new ListKycDocumentsHandler(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private async Task<KycFile> SeedFileAsync()
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock);
        file.SubmitForVerification(_agentId, _clock);
        file.RecordVerification(85, KycConfidenceLevel.High, _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private KycDocument AddDocument(
        Guid fileId, KycDocumentKind kind, int minutesAgo = 0, string? storageRef = null)
    {
        var document = KycDocument.Register(
            _tenantId, fileId, kind,
            storageRef: storageRef ?? $"k1.ref.{Guid.NewGuid():N}",
            contentType: "image/jpeg", sizeBytes: 2048, sha256: new string('b', 64),
            uploadedBy: _agentId,
            clock: new FixedClock(_clock.GetUtcNow().AddMinutes(-minutesAgo)));

        _db.KycDocuments.Add(document);
        return document;
    }

    private Task<Result<KycDocumentListDto>> ListAsync(Guid fileId)
        => _handler.Handle(new ListKycDocumentsQuery(fileId), CancellationToken.None);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── current per kind ────────────────────────────────────────────────────

    [Fact]
    public async Task The_newest_upload_of_a_kind_is_the_current_one()
    {
        var file = await SeedFileAsync();
        var old = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront, minutesAgo: 30);
        var fresh = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront, minutesAgo: 1);
        await _db.SaveChangesAsync();

        var result = await ListAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Documents.Should().HaveCount(2);

        result.Value.Documents.Single(d => d.Id == fresh.Id).IsCurrentForKind.Should().BeTrue();
        result.Value.Documents.Single(d => d.Id == old.Id).IsCurrentForKind
            .Should().BeFalse("a replaced image is kept as history, not judged again");
    }

    [Fact]
    public async Task Each_kind_has_its_own_current_document()
    {
        var file = await SeedFileAsync();
        AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront);
        AddDocument(file.Id, KycDocumentKind.IdentityDocumentBack);
        AddDocument(file.Id, KycDocumentKind.Selfie);
        await _db.SaveChangesAsync();

        var result = await ListAsync(file.Id);

        result.Value.Documents.Where(d => d.IsCurrentForKind).Should().HaveCount(3);
    }

    // ── the "can I decide the file now?" report ─────────────────────────────

    [Fact]
    public async Task Every_current_document_accepted_is_reported_as_such()
    {
        var file = await SeedFileAsync();
        var front = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront);
        var selfie = AddDocument(file.Id, KycDocumentKind.Selfie);
        await _db.SaveChangesAsync();

        front.Review(KycDocumentReviewDecision.Accepted, _validatorId, _clock);
        selfie.Review(KycDocumentReviewDecision.Accepted, _validatorId, _clock);
        await _db.SaveChangesAsync();

        var result = await ListAsync(file.Id);

        result.Value.AllCurrentDocumentsAccepted.Should().BeTrue();
        result.Value.AnyCurrentDocumentNotReviewed.Should().BeFalse();
    }

    [Fact]
    public async Task One_pending_document_is_enough_to_say_no()
    {
        var file = await SeedFileAsync();
        var front = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront);
        AddDocument(file.Id, KycDocumentKind.Selfie);
        await _db.SaveChangesAsync();

        front.Review(KycDocumentReviewDecision.Accepted, _validatorId, _clock);
        await _db.SaveChangesAsync();

        (await ListAsync(file.Id)).Value.AllCurrentDocumentsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task A_superseded_acceptance_does_not_count_for_the_image_that_replaced_it()
    {
        // The decision belongs to the image, so re-uploading resets the question. Otherwise an
        // agent could get a bad photograph through by replacing an accepted one.
        var file = await SeedFileAsync();
        var accepted = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront, minutesAgo: 30);
        await _db.SaveChangesAsync();
        accepted.Review(KycDocumentReviewDecision.Accepted, _validatorId, _clock);
        await _db.SaveChangesAsync();

        AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront, minutesAgo: 1);
        await _db.SaveChangesAsync();

        (await ListAsync(file.Id)).Value.AllCurrentDocumentsAccepted
            .Should().BeFalse("the current image has not been accepted");
    }

    [Fact]
    public async Task A_file_with_no_document_has_not_had_everything_accepted()
    {
        // "All of none" is vacuously true and would offer to validate a file with no evidence.
        var file = await SeedFileAsync();

        var result = await ListAsync(file.Id);

        result.Value.Documents.Should().BeEmpty();
        result.Value.AllCurrentDocumentsAccepted.Should().BeFalse();
    }

    // ── documents that predate the registry ─────────────────────────────────

    [Fact]
    public async Task An_image_collected_before_the_registry_is_shown_as_not_reviewed()
    {
        var file = await SeedFileAsync();

        _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            _tenantId, file.Id, "CNI", "v1:cipher", "blind-index", _clock,
            storageRef: "k1.legacy.front", ocrFieldsJson: """{"surname":"KOUASSI"}"""));

        _db.KycFaceVerifications.Add(KycFaceVerification.Create(
            _tenantId, file.Id, attempt: 1, similarityScore: 0.94, isMatch: true,
            clock: _clock, selfieStorageRef: "k1.legacy.selfie"));

        await _db.SaveChangesAsync();

        var result = await ListAsync(file.Id);

        result.Value.Documents.Should().HaveCount(2);
        result.Value.Documents.Should().AllSatisfy(d =>
        {
            d.Id.Should().BeNull("there is no row to address, so it cannot be reviewed");
            d.ReviewDecision.Should().Be(nameof(KycDocumentReviewDecision.NotReviewed));
            d.ContentType.Should().BeNull("never persisted, and not recomputable from ciphertext");
            d.SizeBytes.Should().BeNull();
            d.Sha256.Should().BeNull();
        });

        result.Value.AnyCurrentDocumentNotReviewed.Should().BeTrue();
        result.Value.AllCurrentDocumentsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task A_document_that_was_uploaded_and_then_verified_is_listed_once()
    {
        // The same ref sits in the registry and on the OCR evidence; the registry row is the
        // richer one and the projection must not duplicate it.
        var file = await SeedFileAsync();
        const string Ref = "k1.shared.ref";
        var document = AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront, storageRef: Ref);

        _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            _tenantId, file.Id, "CNI", "v1:cipher", "blind-index", _clock, storageRef: Ref));

        await _db.SaveChangesAsync();

        var result = await ListAsync(file.Id);

        result.Value.Documents.Should().HaveCount(1);
        result.Value.Documents[0].Id.Should().Be(document.Id);
        result.Value.Documents[0].HasOcrReading.Should().BeTrue();
    }

    [Fact]
    public async Task An_uploaded_document_no_verification_has_read_says_so()
    {
        var file = await SeedFileAsync();
        AddDocument(file.Id, KycDocumentKind.IdentityDocumentFront);
        await _db.SaveChangesAsync();

        (await ListAsync(file.Id)).Value.Documents[0].HasOcrReading.Should().BeFalse();
    }

    // ── isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Documents_of_another_file_are_not_listed()
    {
        var file = await SeedFileAsync();
        var other = await SeedFileAsync();
        AddDocument(file.Id, KycDocumentKind.Selfie);
        AddDocument(other.Id, KycDocumentKind.Selfie);
        await _db.SaveChangesAsync();

        (await ListAsync(file.Id)).Value.Documents.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
        => (await ListAsync(Guid.NewGuid())).Error.Should().Be(KycErrors.FileNotFound);
}
