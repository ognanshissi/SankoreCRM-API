namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Globalization;
using System.Text;

/// <summary>
/// The opt-out keywords of WA-BE-18 — « STOP / ARRET ».
///
/// <para>
/// <b>The whole message, not a word in it.</b> « Ne stoppez pas mon dossier » is not an opt-out,
/// and a false positive withdraws a consent the customer never meant to withdraw. Accents, case,
/// surrounding spaces and a final punctuation mark are ignored: « Arrêt ! » is ARRET.
/// </para>
/// </summary>
public static class WhatsAppOptOut
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "STOP", "ARRET", "ARRETER", "ARRETEZ", "DESABONNER", "DESINSCRIPTION", "UNSUBSCRIBE",
    };

    public static bool IsStop(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;

        var normalized = new StringBuilder(text.Length);

        foreach (var c in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(c)) normalized.Append(char.ToUpperInvariant(c));
            else if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c)) return false;
        }

        return Keywords.Contains(normalized.ToString());
    }
}
