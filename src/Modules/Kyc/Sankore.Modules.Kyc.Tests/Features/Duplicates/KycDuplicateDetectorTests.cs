namespace Sankore.Modules.Kyc.Tests.Features.Duplicates;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Duplicates;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Crypto;
using Xunit;

/// <summary>
/// KYC-B-04, duplicate side.
///
/// The indexer is the REAL <see cref="HmacBlindIndexer"/>: the normalisation it applies is half
/// of what these tests are about, and a stub returning the string unchanged would let a
/// "CI 0012-345678 does not match ci0012345678" regression through unnoticed.
/// </summary>
public sealed class KycDuplicateDetectorTests : IDisposable
{
    private const string Number = "CI0012345678";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly IBlindIndexer _indexer;
    private readonly KycDuplicateDetector _detector;
    private readonly TimeProvider _clock = TimeProvider.System;

    public KycDuplicateDetectorTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _indexer = new HmacBlindIndexer(Options.Create(new FieldProtectionOptions
        {
            SectionName = "Kyc",
            FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

        _detector = new KycDuplicateDetector(_db, _indexer, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private KycFile AddFile(Guid? tenantId = null)
    {
        var file = KycFile.Open(
            tenantId ?? _tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock);
        _db.KycFiles.Add(file);
        return file;
    }

    private void AddDocument(KycFile file, string number)
        => _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            tenantId: file.TenantId,
            kycFileId: file.Id,
            docType: "CNI",
            encryptedNumber: "irrelevant-nothing-here-is-ever-decrypted",
            numberBlindIndex: _detector.ComputeBlindIndex(number),
            clock: _clock));

    /// <summary>
    /// Collecting cannot go straight to Rejected — the transition table routes a refusal through
    /// the verification and the approval circuit. Two different actors because the aggregate
    /// enforces four-eyes on a rejection.
    /// </summary>
    private static void Close(KycFile file, TimeProvider clock)
    {
        file.SubmitForVerification(Guid.NewGuid(), clock);
        file.RecordVerification(90, KycConfidenceLevel.High, clock);
        file.Reject(Guid.NewGuid(), clock).IsSuccess.Should().BeTrue();
    }

    private async Task<KycFile> ReloadAsync(Guid id)
        => await _db.KycFiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == id);

    [Fact]
    public async Task A_document_already_on_another_open_file_of_the_tenant_is_a_collision()
    {
        var existing = AddFile();
        AddDocument(existing, Number);

        var incoming = AddFile();
        AddDocument(incoming, Number);
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, incoming.Id, Number, CancellationToken.None);

        collision.Should().Be(existing.Id);
    }

    [Fact]
    public async Task Flagging_a_collision_raises_the_vigilance_level_to_High()
    {
        // Not cosmetic: High is what adds the compliance officer to the approval circuit, so the
        // flag and the level are one act.
        var existing = AddFile();
        AddDocument(existing, Number);

        var incoming = AddFile();
        AddDocument(incoming, Number);
        await _db.SaveChangesAsync();

        await _detector.DetectForNumberAsync(_tenantId, incoming.Id, Number, CancellationToken.None);

        // The detector deliberately does not save: the flag commits with the caller's work.
        await _db.SaveChangesAsync();

        var flagged = await ReloadAsync(incoming.Id);
        flagged.DuplicateSuspected.Should().BeTrue();
        flagged.VigilanceLevel.Should().Be(KycVigilanceLevel.High);

        // The OTHER file is not flagged: it did nothing, and flagging it would re-open a customer
        // whose file may already be approved.
        (await ReloadAsync(existing.Id)).DuplicateSuspected.Should().BeFalse();
    }

    [Fact]
    public async Task A_collision_in_another_tenant_is_invisible()
    {
        // The one that would be a leak: telling tenant A that tenant B already holds this card
        // reveals that the person is a customer there.
        var foreign = AddFile(_otherTenantId);
        AddDocument(foreign, Number);

        var incoming = AddFile();
        AddDocument(incoming, Number);
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, incoming.Id, Number, CancellationToken.None);

        collision.Should().BeNull();
        await _db.SaveChangesAsync();
        (await ReloadAsync(incoming.Id)).DuplicateSuspected.Should().BeFalse();
    }

    [Fact]
    public async Task A_collision_with_a_closed_file_is_ignored()
    {
        // Re-enrolment after a refusal is legitimate and common. Only OPEN files compete for a
        // document number.
        var rejected = AddFile();
        AddDocument(rejected, Number);
        Close(rejected, _clock);

        var incoming = AddFile();
        AddDocument(incoming, Number);
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, incoming.Id, Number, CancellationToken.None);

        collision.Should().BeNull();
    }

    [Fact]
    public async Task A_file_is_never_its_own_duplicate()
    {
        // The document being checked is already stored by the time detection runs; without the
        // exclusion every single file would flag itself.
        var file = AddFile();
        AddDocument(file, Number);
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, file.Id, Number, CancellationToken.None);

        collision.Should().BeNull();
        await _db.SaveChangesAsync();
        (await ReloadAsync(file.Id)).DuplicateSuspected.Should().BeFalse();
        (await ReloadAsync(file.Id)).VigilanceLevel.Should().Be(KycVigilanceLevel.Standard);
    }

    [Fact]
    public async Task A_number_nobody_has_seen_is_not_a_collision()
    {
        var existing = AddFile();
        AddDocument(existing, Number);

        var incoming = AddFile();
        AddDocument(incoming, "CI0099999999");
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, incoming.Id, "CI0099999999", CancellationToken.None);

        collision.Should().BeNull();
    }

    [Fact]
    public async Task The_same_card_typed_two_ways_still_matches_itself()
    {
        // Spaces, dashes and case are typing, not identity. Without normalisation the detection
        // would quietly find nothing for the commonest data-entry variation there is.
        var existing = AddFile();
        AddDocument(existing, "CI 0012-345678");

        var incoming = AddFile();
        AddDocument(incoming, "ci0012345678");
        await _db.SaveChangesAsync();

        var collision = await _detector.DetectForNumberAsync(
            _tenantId, incoming.Id, " ci0012345678 ", CancellationToken.None);

        collision.Should().Be(existing.Id);
    }

    [Fact]
    public void The_blind_index_is_not_the_number()
    {
        // Equality on this column is the whole search: it must be a hash, never the value, or the
        // index would be a plaintext copy of every document number in the tenant.
        var index = _detector.ComputeBlindIndex(Number);

        index.Should().NotContain(Number);
        index.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
