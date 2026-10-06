namespace Sankore.Modules.Kyc.Tests.Features.Documents;

using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Documents.ReadDocument;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// Reading a KYC image is more sensitive than reading the encrypted number beside it: the picture
/// carries the number, the face and the address at once. Two rules matter here — a reference only
/// opens through the file it belongs to, and the read is recorded before the bytes move.
/// </summary>
public sealed class ReadKycDocumentTests : IDisposable
{
    private const string Ref = "k1.abcdef0123456789.0123456789abcdef0123456789abcdef";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public ReadKycDocumentTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_actorId);
        user.TenantId.Returns(_tenantId);
        return user;
    }

    private static IKycDocumentStore StoreWith(string? content)
    {
        var store = Substitute.For<IKycDocumentStore>();
        store.OpenAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(content is null
                ? (Stream?)null
                : new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return store;
    }

    /// <summary>A file carrying an identity document that points at <see cref="Ref"/>.</summary>
    private async Task<KycFile> SeedAsync(string? documentRef = Ref)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), TimeProvider.System);

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);

        if (documentRef is not null)
        {
            db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
                _tenantId, file.Id, "NationalIdCard", "cipher", "blindindex",
                TimeProvider.System, storageRef: documentRef));
        }

        await db.SaveChangesAsync();
        return file;
    }

    private async Task<IResult> ReadAsync(
        Guid kycFileId, string storageRef, IKycDocumentStore? store = null, string? reason = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { TraceIdentifier = "trace-1" });

        return await ReadKycDocumentEndpoint.Handle(
            kycFileId, storageRef, reason,
            _factory.CreateContext(), store ?? StoreWith("image-bytes"),
            User(), TimeProvider.System, accessor, NullLoggerFactory.Instance,
            CancellationToken.None);
    }

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult s ? s.StatusCode ?? StatusCodes.Status200OK : StatusCodes.Status200OK;

    // ── the audit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_read_is_recorded_with_who_what_and_when()
    {
        var file = await SeedAsync();

        await ReadAsync(file.Id, Ref, reason: "Contrôle conformité trimestriel");

        await using var db = _factory.CreateContext();
        var log = await db.KycDocumentAccessLogs.SingleAsync();

        log.KycFileId.Should().Be(file.Id);
        log.StorageRef.Should().Be(Ref);
        log.ActorUserId.Should().Be(_actorId);
        log.CorrelationId.Should().Be("trace-1");
        log.Reason.Should().Be("Contrôle conformité trimestriel");
    }

    [Fact]
    public async Task Two_reads_leave_two_rows()
    {
        // Append-only: the question is "how many times was this opened", not "was it ever".
        var file = await SeedAsync();

        await ReadAsync(file.Id, Ref);
        await ReadAsync(file.Id, Ref);

        await using var db = _factory.CreateContext();
        (await db.KycDocumentAccessLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_read_without_a_stated_reason_is_still_recorded()
    {
        var file = await SeedAsync();

        await ReadAsync(file.Id, Ref);

        await using var db = _factory.CreateContext();
        (await db.KycDocumentAccessLogs.SingleAsync()).Reason.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_read_records_nothing()
    {
        // The log is evidence of access, not of attempts. An attempt that saw no bytes has not
        // disclosed anything, and filling the table with them would bury the real reads.
        var file = await SeedAsync(documentRef: null);

        await ReadAsync(file.Id, Ref);

        await using var db = _factory.CreateContext();
        (await db.KycDocumentAccessLogs.AnyAsync()).Should().BeFalse();
    }

    // ── what may be opened ──────────────────────────────────────────────────

    [Fact]
    public async Task A_reference_that_belongs_to_another_file_cannot_be_read_through_this_one()
    {
        // Without this the file id would be decoration: any valid reference would open through any
        // file the caller may see, and the audit row would name the wrong file.
        await SeedAsync();
        var otherFile = await SeedAsync(documentRef: "k1.1111111111111111.22222222222222222222222222222222");

        var result = await ReadAsync(otherFile.Id, Ref);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task An_unknown_file_is_not_found_and_never_forbidden()
    {
        StatusOf(await ReadAsync(Guid.NewGuid(), Ref)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_reference_the_store_no_longer_holds_is_not_found()
    {
        var file = await SeedAsync();

        var result = await ReadAsync(file.Id, Ref, StoreWith(null));

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_file_of_another_tenant_is_invisible()
    {
        var other = new TestKycDbContextFactory(Guid.NewGuid());
        var foreign = KycFile.Open(
            Guid.NewGuid(), Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), TimeProvider.System);

        await using (var db = other.CreateContext())
        {
            db.KycFiles.Add(foreign);
            await db.SaveChangesAsync();
        }

        StatusOf(await ReadAsync(foreign.Id, Ref)).Should().Be(StatusCodes.Status404NotFound);
        other.Dispose();
    }

    [Fact]
    public async Task A_selfie_is_reachable_too_and_not_only_the_identity_document()
    {
        const string selfieRef = "k1.9999999999999999.8888888888888888888888888888888f";
        var file = await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            db.KycFaceVerifications.Add(KycFaceVerification.Create(
                _tenantId, file.Id, attempt: 1, similarityScore: 0.9, isMatch: true,
                TimeProvider.System, selfieStorageRef: selfieRef));
            await db.SaveChangesAsync();
        }

        var result = await ReadAsync(file.Id, selfieRef);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
    }
    // ── the registry is also a source of ownership ──────────────────────────

    /// <summary>
    /// Until the registry existed, this check could only see refs a VERIFICATION had recorded — so a
    /// freshly uploaded image was unreadable, which made a document impossible to review by hand,
    /// and an IdentityDocumentBack was unreadable permanently because no table ever referenced one.
    /// </summary>
    [Theory]
    [InlineData(KycDocumentKind.IdentityDocumentFront)]
    [InlineData(KycDocumentKind.IdentityDocumentBack)]
    [InlineData(KycDocumentKind.Selfie)]
    public async Task An_uploaded_image_opens_before_any_verification_has_read_it(KycDocumentKind kind)
    {
        // No KycIdentityDocument and no KycFaceVerification: only the registry row the upload wrote.
        var file = await SeedAsync(documentRef: null);

        await using (var db = _factory.CreateContext())
        {
            db.KycDocuments.Add(KycDocument.Register(
                _tenantId, file.Id, kind,
                storageRef: Ref, contentType: "image/jpeg", sizeBytes: 11,
                sha256: new string('c', 64), uploadedBy: _actorId, clock: TimeProvider.System));

            await db.SaveChangesAsync();
        }

        StatusOf(await ReadAsync(file.Id, Ref)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_registry_row_of_another_file_still_does_not_open_through_this_one()
    {
        // The new clause must widen WHICH refs are known, not weaken the rule that a ref opens only
        // through the file it belongs to.
        var file = await SeedAsync(documentRef: null);
        var other = await SeedAsync(documentRef: null);

        await using (var db = _factory.CreateContext())
        {
            db.KycDocuments.Add(KycDocument.Register(
                _tenantId, other.Id, KycDocumentKind.Selfie,
                storageRef: Ref, contentType: "image/jpeg", sizeBytes: 11,
                sha256: new string('c', 64), uploadedBy: _actorId, clock: TimeProvider.System));

            await db.SaveChangesAsync();
        }

        StatusOf(await ReadAsync(file.Id, Ref)).Should().Be(StatusCodes.Status404NotFound);
    }
}
