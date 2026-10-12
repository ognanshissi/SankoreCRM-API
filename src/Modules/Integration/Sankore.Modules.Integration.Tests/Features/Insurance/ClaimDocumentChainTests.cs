namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Claims.Documents;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-09's first criterion end to end: a piece is deposited, scanned, and only then sent.
///
/// <para>
/// The cases that carry the obligation are
/// <see cref="A_piece_is_never_transmitted_before_the_scan_has_run"/>,
/// <see cref="An_INFECTED_piece_loses_its_bytes_and_keeps_its_row"/> and
/// <see cref="An_unscannable_piece_is_retried_rather_than_declared_infected"/> — the last being
/// the difference between the customer's file being dangerous and our own daemon restarting.
/// </para>
/// </summary>
public sealed class ClaimDocumentChainTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 11, 0, 0, TimeSpan.Zero);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly InMemoryClaimStore _store = new();
    private readonly ScriptedScanner _scanner = new();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly List<ClaimDocumentPayload> _sent = [];

    private Guid _commandId = Guid.NewGuid();

    public ClaimDocumentChainTests()
        => _integration.Insurance
            .RequestClaimDocumentAsync(
                Arg.Do<ClaimDocumentPayload>(_sent.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_commandId));

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedClaimAsync(
        ClaimStatus status = ClaimStatus.DocumentsRequired,
        string? externalClaimId = "CLM-CI-0042")
    {
        await using var db = _factory.CreateContext();

        var connection = SeedConnection(db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var policy = PolicyRecord.Create(
            tenantId: Tenant, connectionId: connection.Id, crmCustomerId: Customer,
            externalPolicyId: "POL-CI-0001", policyNumber: "POL-CI-0001",
            insurerProductCode: "ASS-VIE-EMP", status: PolicyStatus.Issued,
            effectiveDate: new DateOnly(2026, 1, 1), premiumAmount: 7_500m, currency: "XOF",
            periodicity: PremiumPeriodicity.Annual, clock: Clock(Now));

        var claim = ClaimRecord.Create(
            tenantId: Tenant, connectionId: connection.Id, policyId: policy.Id,
            crmCustomerId: Customer, occurredOn: new DateOnly(2026, 4, 10),
            nature: "Incendie", declaredBy: Actor, clock: Clock(Now));

        if (externalClaimId is not null)
            claim.RecordInsurerReference(externalClaimId, externalClaimId, Clock(Now));

        if (status != ClaimStatus.Declared)
            claim.ApplySync(status, null, "Constat de police", Clock(Now));

        db.Policies.Add(policy);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();

        return claim.Id;
    }

    private async Task<IResult> UploadAsync(
        Guid claimId,
        string content = "Constat de police du 10 avril.",
        string contentType = "application/pdf",
        string documentKind = "PoliceReport",
        bool requestedByInsurer = true)
    {
        await using var db = _factory.CreateContext();

        return await ClaimDocumentEndpoints.Upload(
            claimId: claimId,
            documentKind: documentKind,
            file: new FormFile(
                new MemoryStream(Encoding.UTF8.GetBytes(content)),
                baseStreamOffset: 0,
                length: Encoding.UTF8.GetByteCount(content),
                name: "file",
                fileName: "constat.pdf")
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType,
            },
            requestedByInsurer: requestedByInsurer,
            db: db,
            store: _store,
            currentUser: User(Tenant),
            clock: Clock(Now),
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);
    }

    private async Task RunJobAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IClaimDocumentStore>(_store);
        services.AddSingleton<IClaimDocumentScanner>(_scanner);
        services.AddSingleton(_integration);
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        await new ScanClaimDocumentsJob(
                new ClaimDocScopeFactory(provider), NullLogger<ScanClaimDocumentsJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);
    }

    private async Task<List<ClaimDocument>> DocumentsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.ClaimDocuments.IgnoreQueryFilters().OrderBy(d => d.UploadedAt).ToListAsync();
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult { StatusCode: { } code } ? code : 200;

    // ── the deposit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_deposited_piece_is_stored_encrypted_and_registered_PENDING()
    {
        var claimId = await SeedClaimAsync();

        var result = await UploadAsync(claimId);

        StatusOf(result).Should().Be(StatusCodes.Status201Created);

        var document = (await DocumentsAsync()).Should().ContainSingle().Subject;
        document.ClaimId.Should().Be(claimId);
        document.DocumentKind.Should().Be("PoliceReport");

        // Never born clean: the scan has to have run.
        document.ScanStatus.Should().Be(DocumentScanStatus.Pending);
        document.IsTransmittable.Should().BeFalse();

        // And the bytes are in the store, not in the row.
        _store.Objects.Should().ContainKey(document.StorageRef);
    }

    [Fact]
    public async Task The_same_file_twice_on_one_dossier_is_refused_and_leaves_no_orphan()
    {
        // Two rows would be two attachments on the insurer's side.
        // ux_ins_claim_document_digest is what makes this true under concurrency; EF InMemory
        // honours no unique index, so the read is the half asserted here — on the row COUNT.
        var claimId = await SeedClaimAsync();

        await UploadAsync(claimId);
        var second = await UploadAsync(claimId);

        StatusOf(second).Should().Be(StatusCodes.Status409Conflict);
        (await DocumentsAsync()).Should().HaveCount(1);

        // The object written for the refused attempt is removed: keeping it would grow the store
        // once per double-click, with nothing pointing at it.
        _store.Objects.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_different_file_on_the_same_dossier_is_a_second_piece()
    {
        var claimId = await SeedClaimAsync();

        await UploadAsync(claimId, content: "Constat");
        var second = await UploadAsync(claimId, content: "Devis de réparation");

        StatusOf(second).Should().Be(StatusCodes.Status201Created);
        (await DocumentsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unknown_dossier_is_NOT_FOUND_and_stores_nothing()
    {
        var result = await UploadAsync(Guid.NewGuid());

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        _store.Objects.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ClaimStatus.Indemnified)]
    [InlineData(ClaimStatus.Refused)]
    public async Task A_SETTLED_dossier_takes_no_further_pieces(ClaimStatus status)
    {
        // The object would sit in the store with nothing ever reading it, attached to a case the
        // insurer has closed.
        var claimId = await SeedClaimAsync(status: status);

        var result = await UploadAsync(claimId);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        _store.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_content_type_the_store_refuses_is_a_400_and_not_a_500()
    {
        var claimId = await SeedClaimAsync();

        var result = await UploadAsync(claimId, contentType: "application/zip");

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
        (await DocumentsAsync()).Should().BeEmpty();
    }

    // ── the antivirus gate ──────────────────────────────────────────────────

    [Fact]
    public async Task A_piece_is_never_transmitted_before_the_scan_has_run()
    {
        // The obligation of criterion 1, asserted as the ORDER rather than as a flag: the piece is
        // uploaded and nothing is queued until a verdict exists.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Verdict = null; // the sweep has not reached it

        _sent.Should().BeEmpty();

        // And even with the job run, an unscanned piece goes nowhere: the scanner answers a
        // failure here, so the transmit pass finds nothing Clean.
        _scanner.Verdict = ClaimScanVerdict.Failed("daemon restarting");
        await RunJobAsync();

        _sent.Should().BeEmpty();
        (await DocumentsAsync()).Single().TransmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_CLEAN_piece_the_insurer_asked_for_is_sent()
    {
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId, requestedByInsurer: true);

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        var document = (await DocumentsAsync()).Single();
        document.ScanStatus.Should().Be(DocumentScanStatus.Clean);
        document.IsTransmittable.Should().BeTrue();
        document.TransmittedAt.Should().Be(Now);
        document.TransmitCommandId.Should().Be(_commandId);

        var payload = _sent.Should().ContainSingle().Subject;
        payload.ExternalClaimId.Should().Be("CLM-CI-0042");
        payload.ClaimDocumentId.Should().Be(document.Id);
        payload.Sha256.Should().Be(document.Sha256);

        // The BYTES are never in the payload — only the store reference. A command row is
        // replayed, logged and reconciled, and a photograph in one would go through all three.
        payload.StorageRef.Should().Be(document.StorageRef);
    }

    [Fact]
    public async Task A_clean_piece_the_insurer_did_NOT_ask_for_is_not_pushed_at_it()
    {
        // A dossier's own up-front pieces travel with the declaration; pushing every scan result
        // would attach documents nobody requested to a claim under review.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId, requestedByInsurer: false);

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        (await DocumentsAsync()).Single().ScanStatus.Should().Be(DocumentScanStatus.Clean);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_INFECTED_piece_loses_its_bytes_and_keeps_its_row()
    {
        // The row is the fact that somebody uploaded it, which is what an incident review needs.
        // The object is what must not survive.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Verdict = ClaimScanVerdict.Infected("Eicar-Test-Signature");
        await RunJobAsync();

        var document = (await DocumentsAsync()).Should().ContainSingle().Subject;
        document.ScanStatus.Should().Be(DocumentScanStatus.Infected);
        document.ScanDetail.Should().Be("Eicar-Test-Signature");
        document.IsTransmittable.Should().BeFalse();

        _store.Objects.Should().NotContainKey(document.StorageRef);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_infected_piece_is_never_re_scanned()
    {
        // Its bytes are gone, so there is nothing left to look at — and a sweep that kept trying
        // would read a missing object every ten minutes for ever.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Verdict = ClaimScanVerdict.Infected("Eicar-Test-Signature");
        await RunJobAsync();

        _scanner.Calls = 0;
        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        _scanner.Calls.Should().Be(0);
        (await DocumentsAsync()).Single().ScanStatus.Should().Be(DocumentScanStatus.Infected);
    }

    [Fact]
    public async Task An_unscannable_piece_is_retried_rather_than_declared_infected()
    {
        // Our outage, not the customer's file. Recording the second as the first would tell an
        // agent their client sent malware because a daemon was restarting.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Verdict = ClaimScanVerdict.Failed("Could not reach the antivirus.");
        await RunJobAsync();

        var afterOutage = (await DocumentsAsync()).Single();
        afterOutage.ScanStatus.Should().Be(DocumentScanStatus.ScanFailed);
        afterOutage.IsTransmittable.Should().BeFalse();

        // The bytes are still there, which is what makes the retry possible.
        _store.Objects.Should().ContainKey(afterOutage.StorageRef);

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        (await DocumentsAsync()).Single().ScanStatus.Should().Be(DocumentScanStatus.Clean);
    }

    [Fact]
    public async Task A_scanner_that_THROWS_is_recorded_as_a_failure_and_the_batch_continues()
    {
        // The port's contract is to answer a verdict rather than throw; this is the belt around
        // it. One misbehaving scanner must not leave the piece Pending with nothing recorded.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Throw = true;

        var act = async () => await RunJobAsync();

        await act.Should().NotThrowAsync();

        (await DocumentsAsync()).Single().ScanStatus.Should().Be(DocumentScanStatus.ScanFailed);
    }

    [Fact]
    public async Task A_piece_whose_object_has_vanished_is_recorded_unscannable_not_clean()
    {
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _store.Objects.Clear();
        _scanner.Verdict = ClaimScanVerdict.Clean();

        await RunJobAsync();

        var document = (await DocumentsAsync()).Single();
        document.ScanStatus.Should().Be(DocumentScanStatus.ScanFailed);
        _scanner.Calls.Should().Be(0, "there was nothing to scan");
    }

    // ── the transmission's own preconditions ────────────────────────────────

    [Fact]
    public async Task A_dossier_with_no_insurer_reference_waits_rather_than_failing()
    {
        // The payload is keyed by the EXTERNAL claim id: a dossier whose acknowledgement has not
        // landed has nothing to attach to. Left alone — not failed, because nothing was attempted.
        var claimId = await SeedClaimAsync(externalClaimId: null);
        await UploadAsync(claimId);

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        var document = (await DocumentsAsync()).Single();
        document.ScanStatus.Should().Be(DocumentScanStatus.Clean);
        document.TransmittedAt.Should().BeNull();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_piece_already_sent_is_not_sent_twice()
    {
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();
        await RunJobAsync();

        _sent.Should().ContainSingle();
    }

    [Fact]
    public async Task An_insurer_that_refuses_the_command_leaves_the_piece_untouched()
    {
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        _integration.Insurance
            .RequestClaimDocumentAsync(
                Arg.Any<ClaimDocumentPayload>(), Arg.Any<CancellationToken>())
            .Returns<IntegrationCommandId>(_ => throw new DomainException(
                $"{IntegrationErrors.CapabilityNotSupported}: this insurer takes no attachments."));

        _scanner.Verdict = ClaimScanVerdict.Clean();

        var act = async () => await RunJobAsync();

        await act.Should().NotThrowAsync();

        var document = (await DocumentsAsync()).Single();
        document.TransmittedAt.Should().BeNull("tomorrow's run tries again");
        document.ScanStatus.Should().Be(DocumentScanStatus.Clean);
    }

    // ── reading a piece back ────────────────────────────────────────────────

    [Fact]
    public async Task Only_a_piece_the_antivirus_passed_can_be_downloaded()
    {
        // Handing an agent a file the scanner flagged — or has not yet looked at — is the one
        // thing the lock exists to prevent.
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        var document = (await DocumentsAsync()).Single();

        await using (var db = _factory.CreateContext())
        {
            var pending = await ClaimDocumentEndpoints.Download(
                claimId, document.Id, db, _store, User(Tenant), CancellationToken.None);

            StatusOf(pending).Should().Be(StatusCodes.Status409Conflict);
        }

        _scanner.Verdict = ClaimScanVerdict.Clean();
        await RunJobAsync();

        await using (var db = _factory.CreateContext())
        {
            var clean = await ClaimDocumentEndpoints.Download(
                claimId, document.Id, db, _store, User(Tenant), CancellationToken.None);

            StatusOf(clean).Should().Be(StatusCodes.Status200OK);
        }
    }

    [Fact]
    public async Task A_piece_cannot_be_read_through_another_dossier_s_route()
    {
        // Both ids are in the predicate; a document id alone would let a piece be reached through
        // the wrong dossier, which is the perimeter a later agency check hangs off.
        var mine = await SeedClaimAsync();
        await UploadAsync(mine);

        var document = (await DocumentsAsync()).Single();

        await using var db = _factory.CreateContext();

        var result = await ClaimDocumentEndpoints.Download(
            Guid.NewGuid(), document.Id, db, _store, User(Tenant), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task The_listing_returns_metadata_and_never_the_bytes()
    {
        var claimId = await SeedClaimAsync();
        await UploadAsync(claimId);

        await using var db = _factory.CreateContext();

        var result = await ClaimDocumentEndpoints.List(claimId, db, CancellationToken.None);

        var documents = result.Should().BeAssignableTo<IValueHttpResult>()
            .Which.Value.Should().BeAssignableTo<List<ClaimDocumentDto>>().Subject;

        var dto = documents.Should().ContainSingle().Subject;
        dto.Sha256.Should().NotBeNullOrEmpty();
        dto.IsTransmittable.Should().BeFalse();

        // The DTO has no byte member at all, which is what makes this structural rather than a
        // promise: a projection cannot leak a field it does not declare.
        typeof(ClaimDocumentDto).GetProperties()
            .Should().NotContain(p => p.PropertyType == typeof(byte[]));
    }

    // ── doubles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// In memory, because these cases are about the CHAIN. The store's own compliance properties —
    /// the ciphertext on the volume, the tenant binding, the crafted references — are pinned in
    /// <c>EncryptedClaimDocumentStoreTests</c> against the real filesystem backend.
    /// </summary>
    private sealed class InMemoryClaimStore : IClaimDocumentStore
    {
        internal Dictionary<string, byte[]> Objects { get; } = [];

        public async Task<StoredClaimDocument> StoreAsync(
            Guid tenantId, Guid claimId, Stream content, string contentType, CancellationToken ct)
        {
            // The one refusal the chain's cases rely on, so a 400 is reachable without the real
            // store: everything else about content types lives in the store's own tests.
            if (contentType is not ("application/pdf" or "image/jpeg" or "image/png" or "image/heic"))
                throw new DomainException($"{ClaimDocumentErrors.ContentTypeNotAllowed}: {contentType}");

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            if (bytes.Length == 0) throw new DomainException(ClaimDocumentErrors.Empty);

            var storageRef = $"c1.{tenantId:N}"[..19] + "." + Guid.NewGuid().ToString("N");
            Objects[storageRef] = bytes;

            var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            return new StoredClaimDocument(storageRef, contentType, bytes.Length, sha);
        }

        public Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(storageRef, out var bytes) ? bytes : null);

        public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
            => Task.FromResult(Objects.Remove(storageRef));
    }

    private sealed class ScriptedScanner : IClaimDocumentScanner
    {
        internal ClaimScanVerdict? Verdict { get; set; }

        internal bool Throw { get; set; }

        internal int Calls { get; set; }

        public Task<ClaimScanVerdict> ScanAsync(byte[] plaintext, CancellationToken ct)
        {
            Calls++;

            if (Throw) throw new InvalidOperationException("the scanner misbehaved");

            return Task.FromResult(
                Verdict ?? ClaimScanVerdict.Failed("no verdict scripted"));
        }
    }

    private sealed class ClaimDocScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
