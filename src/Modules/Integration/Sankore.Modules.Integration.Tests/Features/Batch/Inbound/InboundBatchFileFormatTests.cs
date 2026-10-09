namespace Sankore.Modules.Integration.Tests.Features.Batch.Inbound;

using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Xunit;

/// <summary>
/// The envelope of OUR convention (INT-25). These tests are what makes the format a contract
/// rather than a comment: the day an integrator hands over a real layout, a change here is the
/// visible cost of it.
/// </summary>
public sealed class InboundBatchFileFormatTests
{
    [Fact]
    public void A_well_formed_header_is_read()
    {
        var bytes = InboundBatchTestContext.File(
            InboundFileKind.Acknowledgement, 7, "ignored;OK;X1;;");

        InboundBatchFileFormat.TrySplit(bytes, out var headerLine, out var body).Should().BeTrue();
        InboundBatchFileFormat.TryParseHeader(headerLine, out var header, out var failure)
            .Should().BeTrue();

        failure.Should().BeNull();
        header.Kind.Should().Be(InboundFileKind.Acknowledgement);
        header.SequenceNo.Should().Be(7);
        header.FormatVersion.Should().Be(InboundBatchFileFormat.FormatVersion);

        // The digest the builder computed is the digest of the body the reader sees. If this ever
        // disagrees, every "happy path" test in this folder is silently exercising the refusal.
        InboundBatchFileFormat.ComputeBodyChecksum(body.Span)
            .Should().Be(header.BodyChecksumSha256);
    }

    [Fact]
    public void A_crlf_header_hashes_the_same_body_as_an_lf_one()
    {
        // The \r belongs to the header line, not to the body: a Windows sender and a Unix sender
        // must compute the same digest or half the integrators can never produce a valid file.
        var lf = Encoding.UTF8.GetBytes("H\nA;B\n");
        var crlf = Encoding.UTF8.GetBytes("H\r\nA;B\n");

        InboundBatchFileFormat.TrySplit(lf, out _, out var lfBody).Should().BeTrue();
        InboundBatchFileFormat.TrySplit(crlf, out _, out var crlfBody).Should().BeTrue();

        InboundBatchFileFormat.ComputeBodyChecksum(lfBody.Span)
            .Should().Be(InboundBatchFileFormat.ComputeBodyChecksum(crlfBody.Span));
    }

    [Theory]
    [InlineData("NOTOURS;1;ACK;1;" + Digest)]
    [InlineData("SNKBATCH;2;ACK;1;" + Digest)]
    [InlineData("SNKBATCH;1;WHAT;1;" + Digest)]
    [InlineData("SNKBATCH;1;ACK;0;" + Digest)]
    [InlineData("SNKBATCH;1;ACK;-3;" + Digest)]
    [InlineData("SNKBATCH;1;ACK;abc;" + Digest)]
    [InlineData("SNKBATCH;1;ACK;1;tooshort")]
    [InlineData("SNKBATCH;1;ACK;1")]
    public void A_header_that_is_not_ours_is_refused_with_a_reason(string headerLine)
    {
        InboundBatchFileFormat
            .TryParseHeader(Encoding.ASCII.GetBytes(headerLine), out _, out var failure)
            .Should().BeFalse();

        failure.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_file_without_a_line_terminator_cannot_be_split()
        => InboundBatchFileFormat
            .TrySplit(Encoding.UTF8.GetBytes("SNKBATCH;1;ACK;1;x"), out _, out _)
            .Should().BeFalse();

    [Theory]
    [InlineData(null, ";")]
    [InlineData("", ";")]
    [InlineData("||", ";")]
    [InlineData("\t", "\t")]
    [InlineData("|", "|")]
    public void The_body_separator_falls_back_unless_it_is_one_character(
        string? configured, string expected)
        => InboundBatchFileFormat.ResolveBodySeparator(configured).Should().Be(expected);

    [Fact]
    public void An_unknown_code_page_falls_back_to_utf8_rather_than_throwing()
        => InboundBatchFileFormat.ResolveEncoding("NOT-A-CODE-PAGE")
            .Should().BeEquivalentTo(Encoding.UTF8);

    private const string Digest =
        "0000000000000000000000000000000000000000000000000000000000000000";
}
