namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Documents.UploadDocument;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The endpoint that was missing: `/verify` consumed storage references nobody could produce, so
/// the whole verification chain was unreachable from the API. What is worth pinning here is not
/// the HTTP plumbing but the two rules the endpoint owns — which statuses may still receive an
/// image, and that a store refusal reaches the caller as a 400 rather than a 500.
/// </summary>
public sealed class UploadKycDocumentTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public UploadKycDocumentTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.TenantId.Returns(_tenantId);
        return user;
    }

    private static IFormFile Image(string content = "fake-jpeg-bytes", string type = "image/jpeg")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "piece.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = type,
        };
    }

    private async Task<KycFile> SeedAsync(KycFileStatus status)
    {
        var submitter = Guid.NewGuid();
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, submitter, TimeProvider.System);

        if (status is not KycFileStatus.Collecting)
        {
            file.SubmitForVerification(submitter, TimeProvider.System);

            if (status is KycFileStatus.ComplementRequired)
                file.RecordVerification(10, KycConfidenceLevel.Rejected, TimeProvider.System);
            else if (status is not KycFileStatus.Verifying)
            {
                file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System);
                if (status is KycFileStatus.Full)
                    file.Approve(KycTier.Full, Guid.NewGuid(), TimeProvider.System);
                else if (status is KycFileStatus.Rejected)
                    file.Reject(Guid.NewGuid(), TimeProvider.System);
            }
        }

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    private async Task<IResult> UploadAsync(
        Guid kycFileId, IFormFile file, IKycDocumentStore? store = null)
        => await UploadKycDocumentEndpoint.Handle(
            kycFileId, KycDocumentKind.IdentityDocumentFront, file,
            _factory.CreateContext(),
            store ?? StoreReturning("k1.abc.def"),
            User(), NullLoggerFactory.Instance, CancellationToken.None);

    private static IKycDocumentStore StoreReturning(string storageRef)
    {
        var store = Substitute.For<IKycDocumentStore>();
        store.StoreAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<KycDocumentKind>(),
                Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new KycStoredDocument(storageRef, "image/jpeg", 42, "sha"));
        return store;
    }

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult s ? s.StatusCode ?? 0 : 0;

    // ── which statuses may still receive an image ───────────────────────────

    [Theory]
    [InlineData(KycFileStatus.Collecting)]
    [InlineData(KycFileStatus.ComplementRequired)]
    [InlineData(KycFileStatus.Verifying)]
    public void The_three_live_statuses_accept_an_image(KycFileStatus status)
        => UploadKycDocumentEndpoint.CanReceiveDocuments(status).Should().BeTrue();

    [Theory]
    [InlineData(KycFileStatus.Validating)]
    [InlineData(KycFileStatus.Simplified)]
    [InlineData(KycFileStatus.Full)]
    [InlineData(KycFileStatus.UnderReview)]
    [InlineData(KycFileStatus.Expired)]
    [InlineData(KycFileStatus.Rejected)]
    [InlineData(KycFileStatus.Suspended)]
    public void A_decided_file_refuses_one(KycFileStatus status)
        => UploadKycDocumentEndpoint.CanReceiveDocuments(status).Should().BeFalse();

    [Fact]
    public async Task Uploading_to_a_validated_file_is_a_conflict_and_stores_nothing()
    {
        // An image attached to a decided case is evidence nobody will read, on a file nobody will
        // re-verify.
        var file = await SeedAsync(KycFileStatus.Full);
        var store = StoreReturning("k1.x.y");

        var result = await UploadAsync(file.Id, Image(), store);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        await store.DidNotReceive().StoreAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<KycDocumentKind>(),
            Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_collecting_file_takes_the_image_and_returns_its_reference()
    {
        var file = await SeedAsync(KycFileStatus.Collecting);

        var result = await UploadAsync(file.Id, Image());

        StatusOf(result).Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task The_reference_is_what_verify_consumes()
    {
        var file = await SeedAsync(KycFileStatus.Collecting);

        var result = await UploadAsync(file.Id, Image(), StoreReturning("k1.deadbeef.cafe"));

        var value = result.Should().BeAssignableTo<IValueHttpResult>().Subject.Value;
        value.Should().BeOfType<UploadKycDocumentResponse>()
            .Which.StorageRef.Should().Be("k1.deadbeef.cafe");
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_file_is_not_found_and_never_forbidden()
    {
        // 404 and not 403: telling a caller "forbidden" would confirm the file exists elsewhere.
        var result = await UploadAsync(Guid.NewGuid(), Image());

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_file_of_another_tenant_is_equally_not_found()
    {
        var other = new TestKycDbContextFactory(Guid.NewGuid());
        var foreign = KycFile.Open(
            Guid.NewGuid(), Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), TimeProvider.System);

        await using (var db = other.CreateContext())
        {
            db.KycFiles.Add(foreign);
            await db.SaveChangesAsync();
        }

        StatusOf(await UploadAsync(foreign.Id, Image())).Should().Be(StatusCodes.Status404NotFound);
        other.Dispose();
    }

    [Fact]
    public async Task An_empty_upload_is_refused_before_the_database_is_touched()
    {
        var empty = new FormFile(new MemoryStream([]), 0, 0, "file", "vide.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };

        StatusOf(await UploadAsync(Guid.NewGuid(), empty)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task A_store_refusal_reaches_the_caller_as_a_400_and_not_a_500()
    {
        // The store rejects an unsupported content type and an oversized file with stable
        // KYC_DOCUMENT_* codes. They are the caller's problem; letting them escape as an
        // unhandled exception would tell an agent nothing and page somebody at night.
        var file = await SeedAsync(KycFileStatus.Collecting);

        var store = Substitute.For<IKycDocumentStore>();
        store.StoreAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<KycDocumentKind>(),
                Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<KycStoredDocument>(_ => throw new DomainException("KYC_DOCUMENT_CONTENT_TYPE_NOT_ALLOWED"));

        StatusOf(await UploadAsync(file.Id, Image(type: "image/gif"), store))
            .Should().Be(StatusCodes.Status400BadRequest);
    }
}
