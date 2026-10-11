namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// CIMA LBC/FT article 13.6: a relationship entered into REMOTELY requires, alongside the identity
/// document, « une quittance de moins de trois mois attestant du domicile ». Before this there was
/// no way to record one at all — <c>KycDocumentKind</c> knew only two identity faces and a selfie.
/// </summary>
public sealed class RemoteSaleEvidenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContextHolder _holder;

    public RemoteSaleEvidenceTests()
    {
        _factory = new TestKycDbContextFactory(_tenant);
        _holder = new KycDbContextHolder(_factory.CreateContext());
    }

    public void Dispose()
    {
        _holder.Dispose();
        _factory.Dispose();
    }

    private sealed class KycDbContextHolder(Sankore.Modules.Kyc.Infrastructure.KycDbContext db)
        : IDisposable
    {
        public Sankore.Modules.Kyc.Infrastructure.KycDbContext Db { get; } = db;
        public void Dispose() => Db.Dispose();
    }

    private async Task<KycFile> SeedFileAsync(KycChannel channel)
    {
        var file = KycFile.Open(_tenant, Guid.NewGuid(), channel, _actor, new FixedClock(Now));
        _holder.Db.KycFiles.Add(file);
        await _holder.Db.SaveChangesAsync();
        return file;
    }

    private async Task AddProofAsync(
        Guid fileId,
        DateOnly? issuedOn,
        KycDocumentReviewDecision decision = KycDocumentReviewDecision.Accepted,
        KycDocumentKind kind = KycDocumentKind.ProofOfAddress)
    {
        var document = KycDocument.Register(
            tenantId: _tenant,
            kycFileId: fileId,
            kind: kind,
            storageRef: $"k.{Guid.NewGuid():N}",
            contentType: "application/pdf",
            sizeBytes: 1024,
            sha256: new string('0', 64),
            uploadedBy: _actor,
            clock: new FixedClock(Now),
            documentIssuedOn: issuedOn);

        if (decision != KycDocumentReviewDecision.Pending)
            document.Review(decision, _actor, new FixedClock(Now), reason: "test").IsSuccess.Should().BeTrue();

        _holder.Db.KycDocuments.Add(document);
        await _holder.Db.SaveChangesAsync();
    }

    private async Task<Sankore.Shared.Kernel.Result> CheckAsync(KycFile file, int maxAgeDays = 90)
        => await TestRemoteSaleEvidence.For(_holder.Db, new FixedClock(Now), maxAgeDays)
            .EnsureSatisfiedAsync(file, CancellationToken.None);

    [Theory]
    [InlineData(KycChannel.Agency)]
    [InlineData(KycChannel.Import)]
    [InlineData(KycChannel.LeadConversion)]
    public async Task A_file_opened_in_person_needs_no_proof_of_address(KycChannel channel)
    {
        // Article 13.6 is about a REMOTE relationship. Requiring the quittance everywhere would
        // be the institution inventing a rule, and a counter could not satisfy it on the spot.
        var file = await SeedFileAsync(channel);

        (await CheckAsync(file)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(KycChannel.Web)]
    [InlineData(KycChannel.MobileAgent)]
    public async Task A_remote_file_with_no_proof_is_refused(KycChannel channel)
    {
        // Which channels are remote is already modelled, so there is nothing to infer.
        var file = await SeedFileAsync(channel);

        var result = await CheckAsync(file);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.ProofOfAddressRequired);
    }

    [Fact]
    public async Task A_remote_file_with_a_recent_accepted_proof_is_satisfied()
    {
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(file.Id, issuedOn: Today.AddDays(-30));

        (await CheckAsync(file)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_proof_older_than_the_window_does_not_satisfy_it()
    {
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(file.Id, issuedOn: Today.AddDays(-120));

        (await CheckAsync(file, maxAgeDays: 90)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_proof_with_NO_issue_date_does_not_satisfy_it()
    {
        // Reading null as "fresh" would pass every row recorded before the column existed, which
        // is the one outcome a freshness check must not have: the document on file may be years
        // old.
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(file.Id, issuedOn: null);

        (await CheckAsync(file)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_REFUSED_proof_does_not_satisfy_it()
    {
        // A refused image is evidence the agent was asked for a better one. Counting it would let
        // a rejected utility bill open an account.
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(
            file.Id, issuedOn: Today.AddDays(-10), decision: KycDocumentReviewDecision.Refused);

        (await CheckAsync(file)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_proof_still_awaiting_review_does_not_satisfy_it()
    {
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(
            file.Id, issuedOn: Today.AddDays(-10), decision: KycDocumentReviewDecision.Pending);

        (await CheckAsync(file)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task An_identity_document_is_not_a_proof_of_address()
    {
        // The kinds are distinct for a reason: a proof of address never enters the OCR or
        // face-match chain, and an identity card does not attest to a domicile.
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(
            file.Id, issuedOn: Today.AddDays(-10),
            kind: KycDocumentKind.IdentityDocumentFront);

        (await CheckAsync(file)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_proof_on_ANOTHER_file_does_not_satisfy_this_one()
    {
        var file = await SeedFileAsync(KycChannel.Web);
        var other = await SeedFileAsync(KycChannel.Web);

        await AddProofAsync(other.Id, issuedOn: Today.AddDays(-10));

        (await CheckAsync(file)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task The_window_is_the_tenants_to_set()
    {
        // Three months is what article 13.6 says today; the text is being reformed and a tenant's
        // own supervisor may ask for less, which is why it is a parameter.
        var file = await SeedFileAsync(KycChannel.Web);
        await AddProofAsync(file.Id, issuedOn: Today.AddDays(-45));

        (await CheckAsync(file, maxAgeDays: 90)).IsSuccess.Should().BeTrue();
        (await CheckAsync(file, maxAgeDays: 30)).IsFailure.Should().BeTrue();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
