namespace Sankore.Modules.Kyc.Tests.Features.Verification;

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Features.Verification.RunVerification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KYC-B-03. Most of what is pinned here is the three-way biometric boundary, because that is the
/// part a reasonable-looking refactor collapses: Success, a refused CAPTURE and an unreachable
/// SERVICE all end a run, and treating the last two alike either rejects an honest client for our
/// own outage or asks an agent to re-photograph a card the camera took perfectly.
/// </summary>
public sealed class RunKycVerificationHandlerTests : IDisposable
{
    private const string DocumentRef = "doc-ref";
    private const string SelfieRef = "selfie-ref";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FakeKycDocumentStore _store = new();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IBackgroundJobClient _hangfire = Substitute.For<IBackgroundJobClient>();
    private readonly IFieldEncryptor _encryptor;
    private readonly IBlindIndexer _indexer;

    public RunKycVerificationHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        // The REAL primitives, not substitutes. A fake encryptor that returns its input would make
        // "the number is never stored in clear" pass while the production path stored it in clear.
        var options = Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            BlindIndexKey = Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()),
        });

        _encryptor = new AesGcmFieldEncryptor(options);
        _indexer = new HmacBlindIndexer(options);

        // JPEG magic bytes, so the handler's content-type sniffing sees a real image rather than
        // falling back and hiding a change to it.
        _store.Objects[DocumentRef] = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        _store.Objects[SelfieRef] = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The real settings service over the test context: no row exists for the tenant, so every key
    /// falls back to its compiled-in default — which is exactly the production behaviour for a
    /// tenant the seeder has not reached yet, and what makes the 40-point floor apply here.
    /// </summary>
    private RunKycVerificationHandler Handler(IBiometryClient biometry, int? rejectionFloor = null)
    {
        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.VerificationRejectionFloor,
                Arg.Any<CancellationToken>())
            .Returns(rejectionFloor ?? 40);

        // The approval circuit is started through ISender once the file reaches Validating. These
        // tests are about the verification itself, so the sender is a substitute — but it must be
        // told what to answer: an unconfigured NSubstitute returns null for a Task<Result<T>>, and
        // the handler would read IsFailure off nothing.
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<StartKycApprovalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new StartKycApprovalResult(
                Guid.NewGuid(), ["Agent", "BranchManager"], AlreadyStarted: false, WorkflowInstanceId: null)));

        return new(_db, _store, biometry,
            new BiometryPayloadProtector(_encryptor, NullLogger<BiometryPayloadProtector>.Instance),
            _encryptor, _indexer, _publisher, _hangfire,
            settings, sender, TimeProvider.System,
            NullLogger<RunKycVerificationHandler>.Instance);
    }

    private async Task<KycFile> SeedFileAsync(KycFileStatus status = KycFileStatus.Collecting)
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, TimeProvider.System);

        // Driven through the aggregate rather than poked into place: a status reached any other
        // way is a status the transition table never approved, and the test would be pinning a
        // state production cannot produce.
        if (status is not KycFileStatus.Collecting)
        {
            file.SubmitForVerification(_agentId, TimeProvider.System).IsSuccess.Should().BeTrue();

            switch (status)
            {
                case KycFileStatus.Verifying:
                    break;
                case KycFileStatus.ComplementRequired:
                    file.RequestComplement(TimeProvider.System).IsSuccess.Should().BeTrue();
                    break;
                case KycFileStatus.Validating:
                    file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System)
                        .IsSuccess.Should().BeTrue();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private Task<Result<RunKycVerificationResult>> RunAsync(IBiometryClient biometry, Guid kycFileId)
        => Handler(biometry).Handle(
            new RunKycVerificationCommand(_tenantId, kycFileId, DocumentRef, SelfieRef, _agentId),
            CancellationToken.None);

    private async Task<KycFile> ReloadAsync(Guid kycFileId)
    {
        await using var db = _factory.CreateContext();
        return await db.KycFiles.SingleAsync(f => f.Id == kycFileId);
    }

    private async Task<List<T>> RowsAsync<T>(Guid kycFileId, Func<KycDbContext, IQueryable<T>> set)
        where T : class
    {
        await using var db = _factory.CreateContext();
        return await set(db).ToListAsync();
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_scored_verification_persists_all_three_pieces_of_evidence_and_enters_the_circuit()
    {
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient { Score = 82 };

        var result = await RunAsync(biometry, file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.Scored);
        result.Value.ConfidenceScore.Should().Be(82);
        result.Value.ConfidenceLevel.Should().Be(KycConfidenceLevel.High);

        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);

        var document = (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Should().ContainSingle().Subject;
        document.DocType.Should().Be("CNI");
        document.StorageRef.Should().Be(DocumentRef);

        var face = (await RowsAsync(file.Id, db => db.KycFaceVerifications)).Should().ContainSingle().Subject;
        face.IsMatch.Should().BeTrue();
        face.SelfieStorageRef.Should().Be(SelfieRef);

        var assessment = (await RowsAsync(file.Id, db => db.KycConfidenceAssessments))
            .Should().ContainSingle().Subject;
        assessment.GlobalScore.Should().Be(82);
        assessment.Trigger.Should().Be("VERIFICATION");
    }

    [Fact]
    public async Task Every_record_carries_the_version_of_the_service_that_produced_it()
    {
        // Evidence nobody can attribute to a model version is not evidence: the Flask service is
        // redeployed and its thresholds move, and a decision taken last quarter still has to be
        // explainable against the model that actually took it.
        var file = await SeedFileAsync();

        await RunAsync(new FakeBiometryClient(), file.Id);

        (await RowsAsync(file.Id, db => db.KycIdentityDocuments))
            .Single().ServiceVersion.Should().Be(FakeBiometryClient.ServiceVersion);
        (await RowsAsync(file.Id, db => db.KycFaceVerifications))
            .Single().ModelVersion.Should().Be(FakeBiometryClient.FaceModelVersion);
        (await RowsAsync(file.Id, db => db.KycConfidenceAssessments))
            .Single().ServiceVersion.Should().Be(FakeBiometryClient.ServiceVersion);
    }

    [Fact]
    public async Task A_scored_verification_announces_itself_without_a_single_sensitive_value()
    {
        var file = await SeedFileAsync();

        await RunAsync(new FakeBiometryClient { Score = 82 }, file.Id);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycVerificationCompletedEvent>(e =>
                e.TenantId == _tenantId
                && e.KycFileId == file.Id
                && e.CustomerEntityId == file.CustomerId
                && e.Outcome == "SCORED"
                && e.Status == nameof(KycFileStatus.Validating)
                && e.ConfidenceScore == 82
                && e.ConfidenceLevel == nameof(KycConfidenceLevel.High)
                && e.RejectionCode == null),
            Arg.Any<CancellationToken>());
    }

    // ── A refused score ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_score_below_the_floor_is_a_refusal_and_goes_back_to_the_agent()
    {
        // The service grades Low/Medium/High and never says "refused"; the refusal is M02's call.
        // Unlike a refused capture, it IS a statement about the file, so the score is recorded.
        var file = await SeedFileAsync();

        var result = await RunAsync(new FakeBiometryClient { Score = 20 }, file.Id);

        result.Value.ConfidenceLevel.Should().Be(KycConfidenceLevel.Rejected);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);

        var assessment = (await RowsAsync(file.Id, db => db.KycConfidenceAssessments))
            .Should().ContainSingle().Subject;
        assessment.Level.Should().Be(KycConfidenceLevel.Rejected);
        assessment.GlobalScore.Should().Be(20);
    }

    [Fact]
    public async Task Two_different_faces_are_a_refusal_whatever_the_score_says()
    {
        var file = await SeedFileAsync();

        var result = await RunAsync(
            new FakeBiometryClient { Score = 95, Similarity = 0.11, IsMatch = false }, file.Id);

        result.Value.ConfidenceLevel.Should().Be(KycConfidenceLevel.Rejected);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);
    }

    // ── Rejected vs Unavailable: the distinction this slice exists for ──────

    [Fact]
    public async Task A_refused_CAPTURE_sends_the_file_back_and_records_no_score_at_all()
    {
        // The service worked. Nothing is known about this customer's confidence, so writing a zero
        // would put a failing score in a compliance history where a blurred photo belongs.
        var file = await SeedFileAsync();

        var result = await RunAsync(
            FakeBiometryClient.Rejecting(BiometryCodes.ImageQualityTooLow), file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.CaptureRejected);
        result.Value.Code.Should().Be(BiometryCodes.ImageQualityTooLow);
        result.Value.ConfidenceScore.Should().BeNull();

        var reloaded = await ReloadAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.ComplementRequired);
        reloaded.ConfidenceScore.Should().BeNull();

        (await RowsAsync(file.Id, db => db.KycConfidenceAssessments)).Should().BeEmpty();
        (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Should().BeEmpty();
        (await RowsAsync(file.Id, db => db.KycFaceVerifications)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_capture_is_never_replayed_because_the_same_bytes_give_the_same_answer()
    {
        var file = await SeedFileAsync();

        await RunAsync(FakeBiometryClient.Rejecting(BiometryCodes.NoFaceDetected), file.Id);

        _hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task An_UNREACHABLE_service_leaves_the_file_in_verification_and_decides_nothing()
    {
        // The one outcome that must never look like a rejection: we learned nothing, and the
        // customer did nothing wrong.
        var file = await SeedFileAsync();

        var result = await RunAsync(
            FakeBiometryClient.Unavailable(BiometryCodes.ModelsNotReady), file.Id);

        // Ok and not Fail, deliberately: TransactionBehavior rolls back on a failed Result, which
        // would undo the move into Verifying and leave the replay nothing to resume.
        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.ServiceUnavailable);
        result.Value.Code.Should().Be(BiometryCodes.ModelsNotReady);

        var reloaded = await ReloadAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.Verifying);
        reloaded.ConfidenceScore.Should().BeNull();
        reloaded.ConfidenceLevel.Should().BeNull();
        reloaded.FaceMatchAttempts.Should().Be(0);

        (await RowsAsync(file.Id, db => db.KycConfidenceAssessments)).Should().BeEmpty();
        (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Should().BeEmpty();
        (await RowsAsync(file.Id, db => db.KycFaceVerifications)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_outage_is_never_announced_as_a_fact_about_the_customer()
    {
        var file = await SeedFileAsync();

        await RunAsync(FakeBiometryClient.Unavailable(), file.Id);

        await _publisher.DidNotReceiveWithAnyArgs()
            .PublishAsync<KycVerificationCompletedEvent>(default!, default);
    }

    [Fact]
    public async Task An_outage_queues_the_attempt_again_instead_of_losing_it()
    {
        var file = await SeedFileAsync();

        await RunAsync(FakeBiometryClient.Unavailable(), file.Id);

        _hangfire.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(Sankore.Modules.Kyc.Features.Verification.ReplayKycVerificationJob)),
            Arg.Any<IState>());
    }

    [Fact]
    public async Task The_retry_budget_runs_out_rather_than_the_file_being_rejected()
    {
        // Third attempt: stuck in Verifying for a human to pick up, which is a ticket. The
        // alternative — pushing it to ComplementRequired — is a complaint from a client whose only
        // mistake was walking in during our outage.
        var file = await SeedFileAsync(KycFileStatus.Verifying);

        var result = await Handler(FakeBiometryClient.Unavailable()).Handle(
            new RunKycVerificationCommand(_tenantId, file.Id, DocumentRef, SelfieRef, _agentId, Attempt: 3),
            CancellationToken.None);

        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.ServiceUnavailable);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Verifying);
        _hangfire.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task A_service_that_dies_only_at_the_scoring_step_is_still_an_outage()
    {
        // The OCR and the face comparison both succeeded. Keeping their rows would leave evidence
        // of a verification that never reached a verdict, and the replay would then write them a
        // second time.
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient
        {
            ForcedScore = BiometryResult<ConfidenceScore>.Unavailable(BiometryCodes.Timeout),
        };

        var result = await RunAsync(biometry, file.Id);

        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.ServiceUnavailable);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Verifying);
        (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Should().BeEmpty();
        (await RowsAsync(file.Id, db => db.KycFaceVerifications)).Should().BeEmpty();
    }

    // ── Crypto ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_document_number_appears_nowhere_in_clear_on_the_row_that_holds_it()
    {
        // Serialising the whole entity rather than checking EncryptedNumber: the number also
        // reaches the OCR blob and the MRZ line, and both are plain jsonb columns. Encrypting one
        // column while the one beside it spells the value out is decorative encryption.
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient();
        biometry.Fields["document_number"] = "CI0012345678";

        await RunAsync(biometry, file.Id);

        var document = (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Single();
        var serialised = JsonSerializer.Serialize(document);

        serialised.Should().NotContain("CI0012345678");
        document.EncryptedNumber.Should().StartWith("v1:");
        _encryptor.Decrypt(document.EncryptedNumber).Should().Be("CI0012345678");
    }

    [Fact]
    public async Task The_other_OCR_fields_survive_so_an_investigation_still_has_something_to_read()
    {
        // The number is stripped; the rest must NOT be, or the blob is worth nothing.
        var file = await SeedFileAsync();

        await RunAsync(new FakeBiometryClient(), file.Id);

        var document = (await RowsAsync(file.Id, db => db.KycIdentityDocuments)).Single();
        document.OcrFieldsJson.Should().Contain("OUATTARA");
        document.ExpiryDate.Should().Be(new DateOnly(2030, 1, 31));
    }

    [Fact]
    public async Task One_card_photographed_twice_and_read_differently_still_yields_one_blind_index()
    {
        // "CI 0123-456" and "ci0123456" are the same card. Without normalisation the two hash
        // differently and the document silently stops matching itself — no duplicate is ever
        // detected, and nothing anywhere fails.
        var first = await SeedFileAsync();
        var second = await SeedFileAsync();

        var biometry = new FakeBiometryClient();

        biometry.Fields["document_number"] = "CI 0123-456";
        await RunAsync(biometry, first.Id);

        biometry.Fields["document_number"] = "ci0123456";
        await RunAsync(biometry, second.Id);

        var documents = await RowsAsync(first.Id, db => db.KycIdentityDocuments);
        documents.Should().HaveCount(2);
        documents.Select(d => d.NumberBlindIndex).Distinct().Should().ContainSingle();

        // And the ciphertexts decrypt to one canonical string, so a later reveal or a CBS export
        // does not show two documents where there is one.
        documents.Select(d => _encryptor.Decrypt(d.EncryptedNumber)).Distinct()
            .Should().ContainSingle().Which.Should().Be("CI0123456");
    }

    [Fact]
    public async Task A_reading_with_no_document_number_is_an_unreadable_document_not_a_crash()
    {
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient();
        biometry.Fields.Remove("document_number");

        var result = await RunAsync(biometry, file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Outcome.Should().Be(RunKycVerificationOutcome.CaptureRejected);
        result.Value.Code.Should().Be(BiometryCodes.DocumentUnreadable);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);
    }

    // ── Face-match attempts ─────────────────────────────────────────────────

    [Fact]
    public async Task Each_comparison_increments_the_attempt_counter_the_approval_circuit_reads()
    {
        // The count lives on the FILE, not on a verification: at the tenant's maximum the circuit
        // gains the branch manager, so losing a tick quietly lowers the approval bar.
        var file = await SeedFileAsync();

        // A refused score leaves the file in ComplementRequired, which is a legitimate entry point
        // for the second run — exactly the sequence an agent produces by re-shooting the selfie.
        await RunAsync(new FakeBiometryClient { Score = 20 }, file.Id);
        (await ReloadAsync(file.Id)).FaceMatchAttempts.Should().Be(1);

        await RunAsync(new FakeBiometryClient { Score = 20 }, file.Id);

        (await ReloadAsync(file.Id)).FaceMatchAttempts.Should().Be(2);
        (await RowsAsync(file.Id, db => db.KycFaceVerifications))
            .Select(v => v.Attempt).Should().BeEquivalentTo([1, 2]);
    }

    // ── Status guard ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_already_in_the_approval_circuit_is_refused_with_a_code()
    {
        var file = await SeedFileAsync(KycFileStatus.Validating);

        var result = await RunAsync(new FakeBiometryClient(), file.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.InvalidTransition);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task A_file_sent_back_for_a_better_photo_can_be_verified_again()
    {
        var file = await SeedFileAsync(KycFileStatus.ComplementRequired);

        var result = await RunAsync(new FakeBiometryClient { Score = 82 }, file.Id);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task A_file_left_in_verification_by_an_outage_is_resumable_by_the_replay()
    {
        // The only reason Verifying is an accepted entry point. Refusing it would make the replay
        // job permanently unable to finish what it was queued for.
        var file = await SeedFileAsync(KycFileStatus.Verifying);

        var result = await RunAsync(new FakeBiometryClient { Score = 82 }, file.Id);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task An_unknown_file_is_reported_as_absent_and_nothing_is_called()
    {
        var biometry = new FakeBiometryClient();

        var result = await RunAsync(biometry, Guid.NewGuid());

        result.Error.Should().Be(KycErrors.FileNotFound);
        biometry.Calls.Should().BeEmpty();
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reference_the_store_does_not_know_stops_the_run_before_the_file_moves()
    {
        // The store answers null for "unknown" and for "another tenant's object" alike. Either way
        // nothing was verified, so the file must not be left sitting in Verifying.
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient();

        var result = await Handler(biometry).Handle(
            new RunKycVerificationCommand(_tenantId, file.Id, "unknown-ref", SelfieRef, _agentId),
            CancellationToken.None);

        result.Error.Should().Be(RunKycVerificationErrors.ImageNotFound);
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.Collecting);
        biometry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_selfie_is_handed_to_the_comparison_and_not_the_document_twice()
    {
        var file = await SeedFileAsync();
        var biometry = new FakeBiometryClient();

        await RunAsync(biometry, file.Id);

        biometry.LastDocumentPortrait!.Content.Should().BeEquivalentTo(_store.Objects[DocumentRef]);
        biometry.LastSelfie!.Content.Should().BeEquivalentTo(_store.Objects[SelfieRef]);
    }
}

/// <summary>
/// In-memory stand-in for the encrypted evidence store. A dictionary rather than a substitute
/// because the handler reads each reference exactly once and a substitute handing back the same
/// already-consumed stream twice would fail for a reason that has nothing to do with the test.
/// </summary>
internal sealed class FakeKycDocumentStore : IKycDocumentStore
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public Task<KycStoredDocument> StoreAsync(
        Guid tenantId, Guid kycFileId, KycDocumentKind kind,
        Stream content, string contentType, CancellationToken ct)
        => throw new NotSupportedException("Uploading belongs to its own slice.");

    public Task<Stream?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
        => Task.FromResult<Stream?>(
            Objects.TryGetValue(storageRef, out var bytes) ? new MemoryStream(bytes) : null);

    public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
        => Task.FromResult(Objects.Remove(storageRef));
}
