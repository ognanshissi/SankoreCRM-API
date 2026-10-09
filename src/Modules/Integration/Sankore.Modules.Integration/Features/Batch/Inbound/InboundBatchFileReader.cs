namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

/// <summary>
/// Bytes to records. The only reader of <see cref="InboundBatchFileFormat"/>.
///
/// <para>
/// <b>Rows are all-string and the reader validates nothing but their shape.</b> That is the rule
/// the repository's three importers established: a spreadsheet — or a CBS's file — only ever
/// hands back text, and typed binding would abort a whole file on one bad cell. Whether
/// <c>command_id</c> is a GUID and whether <c>OK</c> carries an external id are decisions of the
/// appliers, which can report one line and carry on; a reader that parsed types would have to
/// choose between throwing and inventing a value.
/// </para>
///
/// <para>
/// <c>InvariantCulture</c>, like every parser in this solution. The file is written by a system
/// in another country and read by a process whose culture is whatever the container was built
/// with: the pair must not depend on either.
/// </para>
///
/// <para>
/// <b>CsvHelper is configured to tokenise leniently and to report nothing itself.</b>
/// <c>BadDataFound</c> and <c>MissingFieldFound</c> are silenced because a thrown
/// <c>BadDataException</c> abandons the file at the first stray quote — exactly the outcome the
/// per-line report exists to avoid. What "malformed" means is then decided here and reported with
/// a line number, which is what an operator can act on.
/// </para>
/// </summary>
internal static class InboundBatchFileReader
{
    /// <summary>
    /// Reads a file whose header has already been verified.
    ///
    /// <para>
    /// Taking the header rather than re-reading it keeps the order of operations honest: the
    /// sequence and the checksum are checked by the caller BEFORE a single record is parsed,
    /// because a file that fails either must not be applied at all.
    /// </para>
    /// </summary>
    public static ParsedInboundBody ReadBody(
        string fileName,
        InboundBatchHeader header,
        ReadOnlySpan<byte> body,
        string? encodingName,
        string? fieldSeparator)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(header);

        var text = InboundBatchFileFormat.ResolveEncoding(encodingName).GetString(body);
        var delimiter = InboundBatchFileFormat.ResolveBodySeparator(fieldSeparator);

        var minimumFields = header.Kind == InboundFileKind.Acknowledgement
            ? InboundBatchFileFormat.Ack.MinimumFieldCount
            : InboundBatchFileFormat.Extraction.MinimumFieldCount;

        var records = new List<InboundBatchRecord>();
        var reports = new List<InboundLineReport>();

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // The body carries no header row: the envelope already said what the records are, and
            // a header row would be one more thing two sides can disagree about.
            HasHeaderRecord = false,
            Delimiter = delimiter,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,

            // Blank lines are what a file acquires at its end, and a sender that pads with one is
            // not sending us an empty record.
            IgnoreBlankLines = true,
        });

        while (csv.Read())
        {
            // RawRow counts every physical row including the blank ones CsvHelper skipped, so
            // +1 for the header line lands on the line an editor shows.
            var fileLine = csv.Parser.RawRow + 1;
            var fields = csv.Parser.Record ?? [];

            if (fields.Length < minimumFields)
            {
                reports.Add(new InboundLineReport(
                    fileName, fileLine, InboundBatchCodes.LineMalformed,
                    $"{fields.Length} field(s) read, {minimumFields} required for a "
                    + $"{header.Kind} record."));

                continue;
            }

            records.Add(new InboundBatchRecord(fileLine, fields));
        }

        return new ParsedInboundBody(records, reports);
    }
}

/// <summary>
/// One body record: its line in the file, and its fields as read. Field POSITIONS are named by
/// <see cref="InboundBatchFileFormat"/>; nothing outside the format class indexes this array by a
/// literal.
/// </summary>
internal sealed record InboundBatchRecord(int FileLine, string[] Fields)
{
    /// <summary>
    /// A field, trimmed, or null when it is absent or blank. Null rather than empty because every
    /// conditional field of the format — the external id of a refusal, the reason of a success —
    /// means "not applicable", and a caller testing for emptiness as well as absence would get it
    /// wrong once.
    /// </summary>
    public string? Field(int index)
    {
        if (index < 0 || index >= Fields.Length) return null;

        var value = Fields[index]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>The records of one file, and the lines that were not records.</summary>
internal sealed record ParsedInboundBody(
    IReadOnlyList<InboundBatchRecord> Records,
    IReadOnlyList<InboundLineReport> Reports);
