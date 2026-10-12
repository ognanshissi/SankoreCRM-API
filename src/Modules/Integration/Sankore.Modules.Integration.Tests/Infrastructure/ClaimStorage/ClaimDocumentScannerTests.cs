namespace Sankore.Modules.Integration.Tests.Infrastructure.ClaimStorage;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Xunit;

/// <summary>
/// The antivirus pass of ASS-09's first criterion, at the two seams the suite can actually reach:
/// how clamd's verdict line is read, and what happens when no scanner is configured.
///
/// <para>
/// The socket plumbing between them is NOT exercised here and the scanner's own remarks say so —
/// a deployment turning it on verifies it once with EICAR, which is what
/// <see cref="An_infected_answer_is_read_as_infected_and_names_the_signature"/> is written
/// around.
/// </para>
/// </summary>
public sealed class ClaimDocumentScannerTests
{
    // ── reading clamd's verdict ─────────────────────────────────────────────

    [Theory]
    [InlineData("stream: OK")]
    [InlineData("stream: OK\0")]
    [InlineData("  stream: OK  ")]
    public void A_clean_answer_is_read_as_clean(string answer)
        => ClamAvClaimDocumentScanner.ParseResponse(answer.TrimEnd('\0'))
            .Status.Should().Be(DocumentScanStatus.Clean);

    [Fact]
    public void An_infected_answer_is_read_as_infected_and_names_the_signature()
    {
        // The EICAR line a deployment uses to verify the chain once.
        var verdict = ClamAvClaimDocumentScanner.ParseResponse(
            "stream: Eicar-Test-Signature FOUND");

        verdict.Status.Should().Be(DocumentScanStatus.Infected);

        // The signature and not the whole line: it is what an operator is shown in scan_detail.
        verdict.Detail.Should().Be("Eicar-Test-Signature");
    }

    [Fact]
    public void A_signature_whose_name_ends_in_OK_is_still_read_as_INFECTED()
    {
        // The FOUND branch is checked BEFORE the OK branch for exactly this: clamd's clean answer
        // ends in OK, and testing for that first would pass a real detection through.
        var verdict = ClamAvClaimDocumentScanner.ParseResponse("stream: Win.Trojan.NOTOK FOUND");

        verdict.Status.Should().Be(DocumentScanStatus.Infected);
        verdict.Detail.Should().Be("Win.Trojan.NOTOK");
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("UNKNOWN COMMAND")]
    [InlineData("something nobody has seen before")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_OUR_failure_and_never_clean(string? answer)
    {
        // The default branch is Failed and never Clean: an answer this code does not understand
        // must not pass a document to an insurer. Same posture as RequiredFieldEvaluator's
        // `_ => false`.
        ClamAvClaimDocumentScanner.ParseResponse(answer)
            .Status.Should().Be(DocumentScanStatus.ScanFailed);
    }

    [Fact]
    public void A_long_error_line_is_truncated_before_it_reaches_the_column()
    {
        // scan_detail is capped at 300 characters, and a daemon answering a page of text would
        // otherwise fail the insert rather than the scan.
        var verdict = ClamAvClaimDocumentScanner.ParseResponse(new string('x', 5_000));

        verdict.Detail!.Length.Should().BeLessThanOrEqualTo(200);
    }

    // ── the address ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("localhost:3310", "localhost", 3310)]
    [InlineData("10.0.0.5:3310", "10.0.0.5", 3310)]
    [InlineData("clamd.internal:9000", "clamd.internal", 9000)]
    public void A_host_and_port_parse(string address, string expectedHost, int expectedPort)
    {
        ClamAvClaimDocumentScanner.TryParseAddress(address, out var host, out var port)
            .Should().BeTrue();

        host.Should().Be(expectedHost);
        port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // No default of 3310: a deployment that typed only a host has not finished configuring, and
    // guessing the port would turn the mistake into a connection timeout somewhere else.
    [InlineData("localhost")]
    [InlineData("localhost:")]
    [InlineData(":3310")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("localhost:not-a-port")]
    public void Anything_else_is_not_an_address(string? address)
        => ClamAvClaimDocumentScanner.TryParseAddress(address, out _, out _).Should().BeFalse();

    // ── no scanner at all ───────────────────────────────────────────────────

    [Fact]
    public async Task With_no_scanner_configured_every_piece_comes_back_UNSCANNED()
    {
        // It fails CLOSED, which is the whole reason this type exists rather than the registration
        // being left absent: a double answering Clean would make the antivirus pass a decoration
        // on a deployment that has none, and no registration at all would leave pieces at Pending
        // with nothing saying why.
        var scanner = new UnconfiguredClaimDocumentScanner(
            NullLogger<UnconfiguredClaimDocumentScanner>.Instance);

        var verdict = await scanner.ScanAsync([1, 2, 3], CancellationToken.None);

        verdict.Status.Should().Be(DocumentScanStatus.ScanFailed);
        verdict.Status.Should().NotBe(DocumentScanStatus.Clean);
        verdict.Detail.Should().Be(UnconfiguredClaimDocumentScanner.Reason);
    }

    [Fact]
    public async Task And_a_piece_it_refused_is_never_transmittable()
    {
        // The gate itself, asserted end to end over the aggregate rather than over the scanner:
        // whatever the scanner says, only Clean opens it.
        var document = ClaimDocument.Create(
            tenantId: Guid.NewGuid(),
            claimId: Guid.NewGuid(),
            documentKind: "PoliceReport",
            fileName: "constat.pdf",
            contentType: "application/pdf",
            sizeBytes: 1024,
            sha256: new string('a', 64),
            storageRef: "c1.0123456789abcdef.0123456789abcdef0123456789abcdef",
            uploadedBy: Guid.NewGuid(),
            clock: TimeProvider.System);

        document.IsTransmittable.Should().BeFalse("a piece is never born clean");

        var scanner = new UnconfiguredClaimDocumentScanner(
            NullLogger<UnconfiguredClaimDocumentScanner>.Instance);

        var verdict = await scanner.ScanAsync([1], CancellationToken.None);

        document.RecordScan(verdict.Status, verdict.Detail, TimeProvider.System);

        document.IsTransmittable.Should().BeFalse();
    }
}
