namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Documents.ReviewDocument;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KYC-F-02 — un validateur accepte ou refuse une pièce.
///
/// <para>
/// The two rules the product owner fixed are what this pins, and they pull in opposite directions:
/// an acceptance must move NOTHING (so the file-level decision stays a deliberate act and the
/// approval ladder keeps its meaning), while a refusal must send the file back for a complement
/// with the motive attached.
/// </para>
/// </summary>
public sealed class ReviewKycDocumentHandlerTests : IDisposable
{
    private const string Motive = "Photo floue, le numéro de la pièce est illisible.";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _validatorId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ReviewKycDocumentHandler _handler;

    public ReviewKycDocumentHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_validatorId);

        _handler = new ReviewKycDocumentHandler(
            _db, _currentUser, _clock, NullLogger<ReviewKycDocumentHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private async Task<(KycFile File, KycDocument Document)> SeedAsync(
        KycFileStatus status = KycFileStatus.Validating,
        KycDocumentKind kind = KycDocumentKind.IdentityDocumentFront)
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock);

        file.SubmitForVerification(_agentId, _clock);

        if (status == KycFileStatus.Validating)
            file.RecordVerification(85, KycConfidenceLevel.High, _clock);
        else if (status == KycFileStatus.ComplementRequired)
            file.RecordVerification(10, KycConfidenceLevel.Rejected, _clock);

        file.Status.Should().Be(status, "the fixture must seed the status the test asked for");

        var document = NewDocument(file.Id, kind);

        _db.KycFiles.Add(file);
        _db.KycDocuments.Add(document);
        await _db.SaveChangesAsync();

        return (file, document);
    }

    /// <param name="ageInMinutes">
    /// Minutes BEFORE now. Negative values therefore put the upload in the future, which is how a
    /// test makes one document unambiguously newer than another.
    /// </param>
    private KycDocument NewDocument(Guid fileId, KycDocumentKind kind, int ageInMinutes = 0) =>
        KycDocument.Register(
            _tenantId, fileId, kind,
            storageRef: $"k1.{Guid.NewGuid():N}"[..35],
            contentType: "image/jpeg", sizeBytes: 1024, sha256: new string('a', 64),
            uploadedBy: _agentId,
            clock: ageInMinutes == 0
                ? _clock
                : new FixedClock(_clock.GetUtcNow().AddMinutes(-ageInMinutes)));

    private Task<Result<ReviewKycDocumentResult>> ReviewAsync(
        Guid fileId, Guid documentId, KycDocumentReviewDecision decision, string? reason = null)
        => _handler.Handle(
            new ReviewKycDocumentCommand(fileId, documentId, decision, reason),
            CancellationToken.None);

    private async Task<KycFile> ReloadFileAsync(Guid id)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycFiles.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private async Task<KycDocument> ReloadDocumentAsync(Guid id)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycDocuments.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── an acceptance moves nothing ─────────────────────────────────────────

    /// <summary>
    /// The product owner's rule, and the single most important thing in this slice: accepting the
    /// last outstanding document does NOT advance the file. Nothing changes status as a side effect
    /// of a document edit, so the ladder keeps its meaning.
    /// </summary>
    [Fact]
    public async Task Accepting_a_document_leaves_the_file_exactly_where_it_was()
    {
        var (file, document) = await SeedAsync(KycFileStatus.Validating);

        var result = await ReviewAsync(file.Id, document.Id, KycDocumentReviewDecision.Accepted);

        result.IsSuccess.Should().BeTrue();

        var reloaded = await ReloadFileAsync(file.Id);
        reloaded.Status.Should().Be(
            KycFileStatus.Validating, "accepting a document must never advance the file");

        var saved = await ReloadDocumentAsync(document.Id);
        saved.ReviewDecision.Should().Be(KycDocumentReviewDecision.Accepted);
        saved.ReviewedBy.Should().Be(_validatorId);
    }

    [Fact]
    public async Task Accepting_every_document_still_leaves_the_file_where_it_was()
    {
        // The same rule, stated for the case a screen would be tempted to auto-advance.
        var (file, front) = await SeedAsync(KycFileStatus.Validating);
        var selfie = NewDocument(file.Id, KycDocumentKind.Selfie);
        _db.KycDocuments.Add(selfie);
        await _db.SaveChangesAsync();

        await ReviewAsync(file.Id, front.Id, KycDocumentReviewDecision.Accepted);
        await ReviewAsync(file.Id, selfie.Id, KycDocumentReviewDecision.Accepted);

        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    // ── a refusal sends the file back ───────────────────────────────────────

    [Fact]
    public async Task Refusing_a_document_sends_the_file_back_for_a_complement_with_the_motive()
    {
        var (file, document) = await SeedAsync(KycFileStatus.Validating);

        var result = await ReviewAsync(
            file.Id, document.Id, KycDocumentReviewDecision.Refused, Motive);

        result.IsSuccess.Should().BeTrue();
        result.Value.FileStatus.Should().Be(nameof(KycFileStatus.ComplementRequired));

        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);

        var saved = await ReloadDocumentAsync(document.Id);
        saved.ReviewDecision.Should().Be(KycDocumentReviewDecision.Refused);
        saved.RefusalReason.Should().Be(Motive, "it is what the agent is told to fix");
    }

    [Fact]
    public async Task A_refusal_with_no_motive_changes_nothing_at_all()
    {
        // The aggregate refuses it, and because the FILE is moved first the test also proves the
        // move is not committed when the decision fails.
        var (file, document) = await SeedAsync(KycFileStatus.Validating);

        var result = await ReviewAsync(file.Id, document.Id, KycDocumentReviewDecision.Refused);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.DocumentReasonRequired);

        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
        (await ReloadDocumentAsync(document.Id)).ReviewDecision
            .Should().Be(KycDocumentReviewDecision.Pending);
    }

    // ── which statuses allow a review ───────────────────────────────────────

    /// <summary>
    /// Validating is the ordinary case. Verifying matters because it is where a file parks when the
    /// biometric service is unreachable, which is exactly when somebody has to look by hand.
    /// </summary>
    [Theory]
    [InlineData(KycFileStatus.Validating)]
    [InlineData(KycFileStatus.Verifying)]
    public async Task A_document_can_be_reviewed_while_the_file_is_open_for_it(KycFileStatus status)
    {
        var (file, document) = await SeedAsync(status);

        var result = await ReviewAsync(file.Id, document.Id, KycDocumentReviewDecision.Accepted);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_still_in_collection_has_nothing_to_refuse_yet()
    {
        // Collecting → ComplementRequired is not in the transition table, and a file in collection
        // is already with the agent — which is what ComplementRequired means.
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock);
        var document = NewDocument(file.Id, KycDocumentKind.IdentityDocumentFront);
        _db.KycFiles.Add(file);
        _db.KycDocuments.Add(document);
        await _db.SaveChangesAsync();

        var result = await ReviewAsync(
            file.Id, document.Id, KycDocumentReviewDecision.Refused, Motive);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.InvalidTransition);
    }

    // ── staleness and attribution ───────────────────────────────────────────

    [Fact]
    public async Task A_document_already_superseded_by_a_newer_upload_cannot_be_refused()
    {
        // Refusing it would send the whole file back over a photograph the agent already replaced —
        // the same reason a stale approval screen is refused rather than allowed to sign.
        var (file, old) = await SeedAsync(KycFileStatus.Validating);

        // Explicitly ten minutes in the future rather than "whatever the clock says a tick later":
        // relying on two consecutive reads of TimeProvider.System differing is how a test that
        // passes today starts failing on a machine fast enough to register both in the same tick.
        _db.KycDocuments.Add(NewDocument(file.Id, old.Kind, ageInMinutes: -10));
        await _db.SaveChangesAsync();

        var result = await ReviewAsync(file.Id, old.Id, KycDocumentReviewDecision.Refused, Motive);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.DocumentNotCurrent);
        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task A_decided_document_cannot_be_decided_again()
    {
        var (file, document) = await SeedAsync(KycFileStatus.Validating);
        (await ReviewAsync(file.Id, document.Id, KycDocumentReviewDecision.Accepted))
            .IsSuccess.Should().BeTrue();

        var again = await ReviewAsync(file.Id, document.Id, KycDocumentReviewDecision.Refused, Motive);

        again.IsFailure.Should().BeTrue();
        again.Error.Should().Be(KycErrors.DocumentAlreadyReviewed);
    }

    [Fact]
    public async Task A_document_belonging_to_another_file_is_simply_absent()
    {
        var (file, _) = await SeedAsync(KycFileStatus.Validating);
        var (_, otherDocument) = await SeedAsync(KycFileStatus.Validating, KycDocumentKind.Selfie);

        var result = await ReviewAsync(
            file.Id, otherDocument.Id, KycDocumentReviewDecision.Accepted);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.DocumentNotFound);
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
        => (await ReviewAsync(Guid.NewGuid(), Guid.NewGuid(), KycDocumentReviewDecision.Accepted))
            .Error.Should().Be(KycErrors.FileNotFound);
}
