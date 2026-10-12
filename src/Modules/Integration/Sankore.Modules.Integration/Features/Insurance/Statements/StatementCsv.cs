namespace Sankore.Modules.Integration.Features.Insurance.Statements;

using System.Buffers;
using System.Globalization;
using System.Text;

/// <summary>
/// The bordereau as a CSV an insurer's back office can open (ASS-10, criterion 2).
///
/// <para>
/// <b>Rendered on demand from the frozen lines, not read from an archived object.</b>
/// <c>InsurerStatement.CsvStorageRef</c> exists for a stored copy and stays null for now: a stored
/// export needs an encrypted object store of its own, and the honest alternative is available
/// today because the lines ARE the snapshot — the same statement re-exported in a year produces
/// the same bytes, which is the property an archive is wanted for in the first place. What is not
/// yet delivered is the archived copy and the PDF, and that is named rather than faked.
/// </para>
///
/// <para>
/// <b>Everything is written with <see cref="CultureInfo.InvariantCulture"/>.</b> A decimal
/// rendered with the process culture arrives at the insurer as <c>7500,00</c> on a fr-FR host and
/// <c>7500.00</c> elsewhere, and the separator is the field separator — so the file silently gains
/// a column. That is the same trap this repository's spreadsheet readers already carry a rule
/// about, and here it lands in a document somebody reconciles by hand.
/// </para>
///
/// <para>
/// <b>No customer identity.</b> The CRM customer id travels (an opaque Guid, which is what the
/// two systems reconcile on) and no name, no phone, no address: a bordereau leaves the
/// institution, and the insurer already holds the identity of the people it covers.
/// </para>
/// </summary>
internal static class StatementCsv
{
    /// <summary>
    /// <c>;</c> and not <c>,</c>: the audience is West-African back offices on Excel configured
    /// for French, where a comma-separated file opens as one column per row.
    /// </summary>
    private const char Separator = ';';

    /// <summary>
    /// The characters that would break a row. A set rather than a chain of <c>Contains</c>: four
    /// needles is one past what <c>IndexOfAny</c>'s char overloads take, and a silent fall back to
    /// the comparer overload is how that limit is usually discovered.
    /// </summary>
    private static readonly SearchValues<char> MustQuote =
        SearchValues.Create([Separator, '"', '\n', '\r']);

    internal static string ContentType => "text/csv; charset=utf-8";

    internal static string FileName(StatementDto statement)
        => $"bordereau-{statement.Period}-{statement.Id:N}.csv";

    internal static byte[] Render(StatementDetailDto detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var sb = new StringBuilder();

        sb.Append("date").Append(Separator)
          .Append("type").Append(Separator)
          .Append("numero_police").Append(Separator)
          .Append("code_produit_assureur").Append(Separator)
          .Append("client_crm").Append(Separator)
          .Append("montant").Append(Separator)
          .Append("devise").Append(Separator)
          .Append("taux_commission").Append(Separator)
          .Append("commission").Append(Separator)
          .Append("reference_cbs")
          .Append('\n');

        foreach (var line in detail.Lines)
        {
            sb.Append(line.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(Separator)
              .Append(line.LineType).Append(Separator)
              .Append(Escape(line.PolicyNumber)).Append(Separator)
              .Append(Escape(line.InsurerProductCode)).Append(Separator)
              .Append(line.CrmCustomerId.ToString("D", CultureInfo.InvariantCulture))
              .Append(Separator)
              .Append(line.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(Separator)
              .Append(line.Currency).Append(Separator)
              .Append(line.CommissionRate.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(Separator)
              .Append(line.CommissionAmount.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(Separator)
              .Append(Escape(line.CbsReference))
              .Append('\n');
        }

        // The totals, as a trailing line. The one thing an insurer's back office checks before
        // anything else is that the detail adds up to the header, so the file carries both rather
        // than leaving the reader to sum a thousand rows.
        var s = detail.Statement;

        sb.Append('\n')
          .Append("TOTAL").Append(Separator)
          .Append(s.Period).Append(Separator)
          .Append("adhesions=").Append(s.SubscriptionCount.ToString(CultureInfo.InvariantCulture))
          .Append(Separator)
          .Append("resiliations=")
          .Append(s.CancellationCount.ToString(CultureInfo.InvariantCulture)).Append(Separator)
          .Append("contre_passations=")
          .Append(s.ReversalCount.ToString(CultureInfo.InvariantCulture)).Append(Separator)
          .Append(s.PremiumCollected.ToString("0.00", CultureInfo.InvariantCulture))
          .Append(Separator)
          .Append(s.Currency).Append(Separator)
          .Append(Separator)
          .Append(s.CommissionAmount.ToString("0.00", CultureInfo.InvariantCulture))
          .Append(Separator)
          .Append("primes_rendues=")
          .Append(s.PremiumReversed.ToString("0.00", CultureInfo.InvariantCulture))
          .Append('\n');

        // A BOM, deliberately: without it Excel on a French locale reads UTF-8 as Windows-1252 and
        // every accented product name arrives mojibake.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// Quotes a field that would otherwise break the row. A policy number or an insurer product
    /// code comes from an external system, so it is not assumed to be free of the separator — and
    /// a leading <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is prefixed with a quote so a
    /// spreadsheet opens the value as text rather than evaluating it as a formula.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var safe = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;

        return safe.AsSpan().IndexOfAny(MustQuote) >= 0
            ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safe;
    }
}
