namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════════════════════
/// <b>THIS IS OUR CONVENTION, NOT A CBS's NATIVE FORMAT — AND IT IS THE FIRST THING TO AGREE
/// WITH AN INTEGRATOR.</b>
/// ════════════════════════════════════════════════════════════════════════════════════════════
///
/// <para>
/// No document in this repository, and none of the four specifications the plan lists
/// (INT-28 Perfect Vision, INT-31 Amplitude, INT-32 SAB, ASS-06 ORASS), defines the layout of an
/// acknowledgement or extraction file. So this slice <b>invents one</b> and says so, the same
/// honesty <c>TemenosWire.cs</c> applies to a wire record it could not obtain: a format guessed
/// in silence is a format every reader believes is specified.
/// </para>
///
/// <para>
/// Everything about the layout lives in this one file — the envelope, the record kinds, the field
/// positions, the tokens. When the integrator hands over the real layout, this file and
/// <see cref="InboundBatchFileReader"/> are what change; the two jobs, the two appliers and their
/// tests do not know a field position exists.
/// </para>
///
/// <para>
/// ── THE FORMAT, v1 ──────────────────────────────────────────────────────────────────────────
/// </para>
/// <code>
/// line 1    SNKBATCH;1;&lt;kind&gt;;&lt;sequence&gt;;&lt;sha256-hex-of-body&gt;
/// line 2..N one record per line, CSV, separated by the connection's FieldSeparator
///
/// kind      ACK | EXTRACT
///
/// ACK       &lt;command_id&gt;;&lt;outcome&gt;;&lt;external_id&gt;;&lt;reason_code&gt;;&lt;reason_detail&gt;
///           outcome  OK → external_id is required, the reason fields are ignored
///                    KO → reason_code is required, external_id is ignored
///
/// EXTRACT   &lt;external_customer_id&gt;[;…further columns, ignored]
/// </code>
///
/// <para>
/// <b>The header is fixed-separator, the body is not.</b> The envelope has to be readable BEFORE
/// any connection setting is consulted — it is what names the sequence a sequence check needs and
/// the checksum a checksum check needs — so it is always <c>;</c>. The body follows the
/// connection's <c>FieldSeparator</c>, because it is the same file the integrator agreed with us
/// and the outbound half already writes with that setting.
/// </para>
///
/// <para>
/// <b>The checksum covers the BODY, not the whole file.</b> A file cannot carry a checksum of
/// itself, and a checksum over the bytes that FOLLOW the first line terminator is the one thing a
/// sender can compute and a reader can verify byte-for-byte without re-encoding anything —
/// deliberately independent of the file's code page, line endings inside the body included.
/// </para>
///
/// <para>
/// <b>An acknowledgement names a command by OUR identifier</b>, the <c>command_id</c> the
/// outbound file carried, and not by the external system's own reference. The external reference
/// is what the acknowledgement TELLS us (<c>external_id</c>); keying on it would mean looking up a
/// row by the value the file exists to deliver. The command id is also the only key that is
/// unforgeable from the outside: it is a v4 GUID, so a neighbouring tenant's file cannot close
/// commands by guessing identifiers — and every lookup is still scoped to the tenant and the
/// connection on top of that.
/// </para>
///
/// <para>
/// There is deliberately <b>no record count in the header</b>. A count that the checksum already
/// guarantees is a second source of truth for the same fact, and a reader that trusted it would
/// have to decide what to do when the two disagree.
/// </para>
/// </summary>
internal static class InboundBatchFileFormat
{
    /// <summary>First field of the header. Anything else is not one of our files at all.</summary>
    public const string Magic = "SNKBATCH";

    /// <summary>
    /// The only version this reader accepts. A higher one is refused rather than read on a
    /// best-effort basis: applying four of five fields of a format we do not know closes commands
    /// on a guess.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// The envelope's separator, fixed. See the type remarks: the header is parsed before the
    /// connection's settings are consulted.
    /// </summary>
    public const char HeaderSeparator = ';';

    /// <summary>Fields of a well-formed header, exactly.</summary>
    public const int HeaderFieldCount = 5;

    /// <summary>Separator used when the connection's settings name none, or name a nonsense one.</summary>
    public const string DefaultBodySeparator = ";";

    /// <summary>Code page assumed when the connection names none, or names an unknown one.</summary>
    public const string DefaultEncodingName = "UTF-8";

    /// <summary>
    /// A refusal, never a truncation — the word <see cref="Infrastructure.Transport"/> uses for
    /// the same parameter. Sixteen mebibytes is roughly 130 000 acknowledgement lines: far beyond
    /// a day's batch for an IMF, and small enough that a file pointed at us by mistake cannot
    /// exhaust a Hangfire worker's memory.
    /// </summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    // ── Record kinds ────────────────────────────────────────────────────────

    public const string AcknowledgementToken = "ACK";
    public const string ExtractionToken = "EXTRACT";

    /// <summary>Field positions of an ACK record, and the minimum a reader insists on.</summary>
    public static class Ack
    {
        public const int CommandId = 0;
        public const int Outcome = 1;
        public const int ExternalId = 2;
        public const int ReasonCode = 3;
        public const int ReasonDetail = 4;

        /// <summary>
        /// The id and the outcome. The three that follow are conditional — a refusal carries no
        /// external id and a success carries no reason — so a reader that demanded five fields
        /// would reject every well-formed line a sender trims.
        /// </summary>
        public const int MinimumFieldCount = 2;
    }

    /// <summary>Field positions of an EXTRACT record.</summary>
    public static class Extraction
    {
        public const int ExternalCustomerId = 0;

        public const int MinimumFieldCount = 1;
    }

    public const string OutcomeSucceeded = "OK";
    public const string OutcomeRejected = "KO";

    // ── Reading the envelope ────────────────────────────────────────────────

    /// <summary>
    /// Splits a file into its header line and the exact body bytes the checksum covers.
    ///
    /// <para>
    /// The split is on the first <c>\n</c> and the body is everything after it, byte-for-byte. A
    /// <c>\r</c> immediately before that terminator belongs to the header line and is trimmed when
    /// the header is parsed — so a CRLF sender and an LF sender compute the same body checksum,
    /// which is the whole point of splitting on bytes rather than re-serialising lines.
    /// </para>
    /// </summary>
    public static bool TrySplit(byte[] content, out ReadOnlySpan<byte> headerLine, out ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(content);

        var newLine = Array.IndexOf(content, (byte)'\n');

        if (newLine < 0)
        {
            headerLine = default;
            body = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        headerLine = content.AsSpan(0, newLine);
        body = new ReadOnlyMemory<byte>(content, newLine + 1, content.Length - newLine - 1);
        return true;
    }

    /// <summary>
    /// Parses the header line, or says why it is not one of ours.
    ///
    /// <para>
    /// <b>The header is ASCII by definition</b> and is decoded as such rather than with the
    /// connection's code page: the magic, the version, the kind, the sequence and a hex digest
    /// contain nothing outside ASCII, and reading the envelope with a code page we might have
    /// configured wrongly would make an unreadable header out of a perfectly good file.
    /// </para>
    /// </summary>
    public static bool TryParseHeader(
        ReadOnlySpan<byte> headerLine, out InboundBatchHeader header, out string? failure)
    {
        header = null!;

        var text = Encoding.ASCII.GetString(headerLine).TrimEnd('\r').Trim();
        var fields = text.Split(HeaderSeparator);

        if (fields.Length != HeaderFieldCount)
        {
            failure = $"The header has {fields.Length} field(s); {HeaderFieldCount} are expected.";
            return false;
        }

        if (!string.Equals(fields[0].Trim(), Magic, StringComparison.Ordinal))
        {
            failure = "The header does not start with the SANKORE batch marker.";
            return false;
        }

        if (!int.TryParse(fields[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != FormatVersion)
        {
            failure = $"Format version '{fields[1].Trim()}' is not supported; only v{FormatVersion} is.";
            return false;
        }

        var kindToken = fields[2].Trim().ToUpperInvariant();

        var kind = kindToken switch
        {
            AcknowledgementToken => (InboundFileKind?)InboundFileKind.Acknowledgement,
            ExtractionToken => InboundFileKind.Extraction,
            _ => null,
        };

        if (kind is null)
        {
            failure = $"'{fields[2].Trim()}' is not a known file kind "
                      + $"({AcknowledgementToken} or {ExtractionToken}).";
            return false;
        }

        if (!long.TryParse(fields[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0)
        {
            failure = $"'{fields[3].Trim()}' is not a sequence number; a sequence starts at 1.";
            return false;
        }

        var checksum = fields[4].Trim().ToLowerInvariant();

        if (checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
        {
            failure = "The header does not carry a 64-character SHA-256 digest.";
            return false;
        }

        header = new InboundBatchHeader(version, kind.Value, sequence, checksum);
        failure = null;
        return true;
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the body, in the spelling
    /// <see cref="Domain.IntegrationBatchFile.ChecksumSha256"/> stores — the factory lower-cases
    /// what it is given, so computing it in any other case would make every comparison a
    /// case-folding question.
    /// </summary>
    public static string ComputeBodyChecksum(ReadOnlySpan<byte> body)
        => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// The connection's code page, or UTF-8.
    ///
    /// <para>
    /// Honoured rather than hardcoded because <c>BatchCapableSettings.FileEncoding</c> exists for
    /// exactly this reason — "West-African CBS deployments are rarely UTF-8" — and an unknown
    /// name falls back instead of throwing: a mistyped code page must not turn into a Hangfire
    /// job that fails for ever on a file that is otherwise readable. The checksum is over bytes,
    /// so the fallback can never make a verified file fail verification.
    /// </para>
    /// </summary>
    public static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(name.Trim());
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// The body's field separator. A setting of more than one character, or none, falls back:
    /// CsvHelper accepts a multi-character delimiter, but the outbound half writes one character
    /// and a reader that silently accepted "||" would disagree with the file it is reading.
    /// </summary>
    public static string ResolveBodySeparator(string? configured)
        => string.IsNullOrEmpty(configured) || configured.Length != 1
            ? DefaultBodySeparator
            : configured;
}

/// <summary>What an inbound file says it is. Two kinds, both polled from the same directory.</summary>
internal enum InboundFileKind
{
    /// <summary>Closes commands the outbound half left <c>Batched</c> (INT-25, criterion 2).</summary>
    Acknowledgement,

    /// <summary>Names the customers a daily extraction covers (INT-25, criterion 4).</summary>
    Extraction
}

/// <summary>The envelope of an inbound file, once verified.</summary>
internal sealed record InboundBatchHeader(
    int FormatVersion,
    InboundFileKind Kind,
    long SequenceNo,
    string BodyChecksumSha256);
