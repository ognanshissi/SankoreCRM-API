namespace Sankore.Modules.Customers.Features.Timeline.Projection;

using System.Text.RegularExpressions;

/// <summary>
/// Last line of defence for the "no sensitive value in a timeline summary" rule
/// (US-M01-BE-26 acceptance criterion).
///
/// The timeline is a read model with NO reveal audit and NO encryption: anything written
/// into <c>Summary</c> is readable by every user holding <c>customers:read</c>. Producers
/// are therefore required to build summaries out of structured, non-sensitive data — but a
/// producer is a consumer of someone else's event, and a future module could carry an
/// operator-typed string. So every summary is passed through here on the way in and any
/// pattern that looks like an e-mail, a phone / document number or a date of birth is
/// redacted rather than stored.
///
/// Design note on the number rules. A client number (<c>AG1-2026-000123</c>) must survive,
/// while a phone (<c>+225 07 11 22 33</c>, <c>07-11-22-33</c>) and a document number
/// (<c>CI0123456789</c>) must not. One regex cannot do both, so there are two, each requiring
/// at least <see cref="MinSensitiveDigits"/> digits in the run:
///   * <see cref="GluedNumberRun"/> treats the dash as a SEPARATOR of runs, which splits
///     <c>AG1-2026-000123</c> into 1 / 2026 / 000123 (6 digits at most, spared) while still
///     catching <c>CI0123456789</c> glued to its letter prefix and <c>+225 07 11 22 33</c>.
///   * <see cref="DashedNumberRun"/> allows the dash INSIDE the run, to catch
///     <c>07-11-22-33</c>, but only when the run is attached to neither a letter, a digit nor
///     a dash on either side — which is what keeps a client number out of its reach.
///
/// GUIDs are lifted out before any number rule runs and put back afterwards, because a GUID
/// group can legitimately be all digits (<c>…-0000-000000000002</c>) and would otherwise be
/// redacted, destroying the reference an operator navigates by. The exemption is safe: no
/// phone, document number, e-mail or date of birth is GUID-shaped.
/// </summary>
internal static partial class TimelineSummaryGuard
{
    /// <summary>What replaces a redacted fragment. Deliberately not a masked value:
    /// a timeline is not a reveal surface, not even a partial one.</summary>
    internal const string Redacted = "•••";

    /// <summary>Shown when a producer passes nothing at all — the column is NOT NULL.</summary>
    internal const string EmptySummary = "(sans détail)";

    /// <summary>Maximum length of the <c>summary</c> column.</summary>
    private const int MaxLength = 500;

    /// <summary>
    /// Digits a run must carry to be treated as a phone or document number. Seven, because the
    /// shortest national subscriber numbers in the region are eight digits, while the longest
    /// digit group of a client number (<c>{Seq:6}</c>) is six.
    /// </summary>
    private const int MinSensitiveDigits = 7;

    /// <summary>
    /// Stand-in for a GUID while the number rules run. A Private Use character: neither a
    /// letter nor a digit for the regexes, and it cannot occur in a real summary.
    /// </summary>
    private const char GuidPlaceholder = '';

    [GeneratedRegex(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}[/.]\d{1,2}[/.]\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(\.[\p{L}\p{N}\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    /// <summary>Digit run where the dash SEPARATES runs (spares client numbers).</summary>
    [GeneratedRegex(@"[+(]?\d[\d\s().]{4,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex GluedNumberRun();

    /// <summary>Digit run where the dash is part of the run, but only when it stands alone.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}\-])[+(]?\d[\d\s().\-]{4,}\d(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex DashedNumberRun();

    /// <summary>
    /// Returns the summary as it may be persisted: redacted, then truncated to the column.
    /// Never throws — a malformed summary must still produce a timeline entry.
    /// </summary>
    internal static string Sanitize(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return EmptySummary;

        var cleaned = Redact(summary).Trim();

        if (cleaned.Length > MaxLength)
            cleaned = string.Concat(cleaned.AsSpan(0, MaxLength - 1), "…");

        return cleaned.Length == 0 ? EmptySummary : cleaned;
    }

    /// <summary>
    /// True when redaction actually removed something — i.e. the producer handed over a value
    /// it should not have. Used to log a warning that makes the offending producer findable.
    /// </summary>
    internal static bool LooksSensitive(string? summary)
        => !string.IsNullOrWhiteSpace(summary)
           && !string.Equals(Redact(summary), summary, StringComparison.Ordinal);

    private static string Redact(string summary)
    {
        // GUIDs out of the way first — see the class remarks.
        var guids = new List<string>();
        var working = GuidPattern().Replace(summary, match =>
        {
            guids.Add(match.Value);
            return GuidPlaceholder.ToString();
        });

        // Order matters: dates first (1987-03-12 would otherwise be eaten by a number rule
        // and reported as a phone), then e-mails (whose local part can hold a digit run),
        // then the remaining long numbers.
        working = DatePattern().Replace(working, Redacted);
        working = EmailPattern().Replace(working, Redacted);
        working = GluedNumberRun().Replace(working, RedactWhenEnoughDigits);
        working = DashedNumberRun().Replace(working, RedactWhenEnoughDigits);

        return guids.Count == 0 ? working : RestoreGuids(working, guids);
    }

    /// <summary>
    /// Puts the GUIDs back in order. A placeholder cannot be swallowed by a redaction (it is
    /// neither digit nor separator), but the bounds check stays: losing a GUID would be a
    /// silent corruption, and a leftover placeholder is at least visible.
    /// </summary>
    private static string RestoreGuids(string working, List<string> guids)
    {
        var parts = working.Split(GuidPlaceholder);
        var builder = new System.Text.StringBuilder(parts[0]);

        for (var i = 1; i < parts.Length; i++)
        {
            if (i - 1 < guids.Count) builder.Append(guids[i - 1]);
            builder.Append(parts[i]);
        }

        return builder.ToString();
    }

    private static string RedactWhenEnoughDigits(Match match)
        => CountDigits(match.Value) >= MinSensitiveDigits ? Redacted : match.Value;

    private static int CountDigits(string value)
    {
        var count = 0;
        foreach (var c in value)
            if (char.IsAsciiDigit(c)) count++;
        return count;
    }
}
