namespace Sankore.Modules.Administration.Infrastructure;

using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Normalises a product code or name for label matching (BNF-B-01): upper-cased, accents removed,
/// inner whitespace collapsed.
///
/// <para>
/// It exists because the two sides of the match are written by different people. A lead's
/// <c>InterestedProduct</c> is typed by an agent or sent by a web form — "credit commercant",
/// "Crédit  Commerçant" — while the catalogue holds whatever an administrator entered. Comparing
/// them raw resolves almost nothing; comparing them loosely would resolve the wrong line, and the
/// resolved line decides which fields gate a conversion.
/// </para>
///
/// <para>
/// Diacritic removal reuses <see cref="SensitiveValueNormalizer.RemoveDiacritics"/> rather than a
/// second implementation — M01's client matching already depends on that exact behaviour, and two
/// normalisers that disagree about "ç" is the kind of difference nobody finds by reading.
/// </para>
/// </summary>
internal static class ProductLabelNormalizer
{
    /// <summary>
    /// The comparable form, or <c>null</c> when there is nothing to compare.
    ///
    /// <para>
    /// <b>"UNKNOWN" resolves to null on purpose.</b> It is the literal default the scheduled-pull
    /// job and the system capture endpoint write when a source names no product, so it is not a
    /// label — it is the absence of one. A tenant unlucky enough to have a product actually named
    /// "Unknown" would otherwise collect every unlabelled lead in the deployment onto its policy.
    /// </para>
    /// </summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var stripped = SensitiveValueNormalizer.RemoveDiacritics(raw).Trim().ToUpperInvariant();

        // Collapse inner runs of whitespace: "Crédit  commerçant" and "Crédit commerçant" are one
        // label as far as any human is concerned.
        var collapsed = string.Join(' ', stripped.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (collapsed.Length == 0)
            return null;

        return collapsed is "UNKNOWN" ? null : collapsed;
    }
}
