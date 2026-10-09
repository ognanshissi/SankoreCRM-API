namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using System.Globalization;
using System.Text;

/// <summary>
/// The fallback format: one delimited line per command, with a header row (INT-24, criterion 2).
///
/// <para>
/// Registered with <c>TryAdd</c>, so an adapter assembly that knows its CBS's real layout
/// replaces it. It is NOT a guess at a CBS layout — see <see cref="IOutboundBatchFormatter"/> for
/// why inventing one would be worse than useless — but a complete, self-describing projection of
/// what the platform owes: the command's identity, its type, the CRM entity it concerns, and its
/// payload fields as columns.
/// </para>
///
/// <para>
/// <b>The column set is the union of the payload field names present in the file, sorted.</b>
/// Sorted so two files of the same connection have the same column order and a diff is readable;
/// a union rather than one file per command type because a day's writes are a handful of rows and
/// an integrator mapping one file is better served than one mapping five. A command missing a
/// column leaves it empty, which is distinguishable from a column it set to the empty string only
/// by the header — a limitation named here rather than papered over with a sentinel, since a
/// sentinel is a value some CBS will eventually accept as data.
/// </para>
///
/// <para>
/// <b>Quoting is RFC 4180</b> over the configured separator: a value containing the separator, a
/// double quote, CR or LF is wrapped in quotes and its quotes doubled. That is not cosmetic — a
/// West-African address runs "Cocody, Riviera 3" and a semicolon-separated file that did not
/// quote it would shift every later column of that row by one, silently, for one customer in
/// fifty.
/// </para>
///
/// <para>
/// Lines end <c>CRLF</c>. The receiving end is a mainframe or a Windows-era batch loader far more
/// often than it is a POSIX tool, and CRLF is what both tolerate.
/// </para>
/// </summary>
internal sealed class DelimitedOutboundBatchFormatter : IOutboundBatchFormatter
{
    public string FileExtension => ".csv";

    /// <summary>
    /// Fixed leading columns, in this order. <c>idempotency_key</c> is present deliberately: it is
    /// what lets the CBS — or an operator reconciling by hand — recognise a row that was already
    /// applied if a file is ever deposited twice.
    /// </summary>
    private static readonly string[] FixedColumns =
    [
        "command_id",
        "command_type",
        "entity_type",
        "crm_id",
        "idempotency_key",
        "created_at",
    ];

    private const string LineEnding = "\r\n";

    public string Render(OutboundBatchContext context, IReadOnlyList<OutboundBatchRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(records);

        var separator = string.IsNullOrEmpty(context.FieldSeparator) ? ";" : context.FieldSeparator;

        var payloadColumns = records
            .SelectMany(r => r.Payload.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();

        builder.Append(string.Join(
            separator, FixedColumns.Concat(payloadColumns).Select(c => Quote(c, separator))));
        builder.Append(LineEnding);

        foreach (var record in records)
        {
            var cells = new List<string>(FixedColumns.Length + payloadColumns.Count)
            {
                record.CommandId.ToString("D", CultureInfo.InvariantCulture),
                record.CommandType.ToString(),
                record.EntityType,
                record.CrmId.ToString("D", CultureInfo.InvariantCulture),
                record.IdempotencyKey,

                // Round-trip ("O"), in UTC. Not a local or short form: this repo has already been
                // bitten by 02/04/1987 meaning two different days on two hosts, and a batch file
                // is read by a parser nobody here controls.
                record.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            };

            cells.AddRange(payloadColumns.Select(
                column => record.Payload.TryGetValue(column, out var value) ? value : string.Empty));

            builder.Append(string.Join(separator, cells.Select(c => Quote(c, separator))));
            builder.Append(LineEnding);
        }

        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting, applied only where it is needed so a file stays readable by a human
    /// opening it in a text editor at 2 a.m.
    /// </summary>
    private static string Quote(string value, string separator)
    {
        if (value.Length == 0) return value;

        var mustQuote = value.Contains('"', StringComparison.Ordinal)
                        || value.Contains('\r', StringComparison.Ordinal)
                        || value.Contains('\n', StringComparison.Ordinal)
                        || value.Contains(separator, StringComparison.Ordinal);

        if (!mustQuote) return value;

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
