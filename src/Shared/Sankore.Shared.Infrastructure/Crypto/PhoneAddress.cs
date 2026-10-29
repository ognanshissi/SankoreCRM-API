namespace Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// A stored telephone value as a dialable E.164 address. Moved out of M01's <c>ClientSmsNotifier</c>
/// (2026-10-29) when M13 started texting leads: one answer to « what number does this mean », not
/// two that drift. Its cases stay in <c>ClientSmsE164Tests</c>.
/// </summary>
public static class PhoneAddress
{
    /// <summary>
    /// The address, or null when the value cannot be one. A local number (no <c>+</c>, no
    /// <c>00</c>) gets the tenant's calling code, and without one it is refused rather than guessed.
    /// </summary>
    public static string? ToE164(string? stored, string? defaultCallingCode)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var trimmed = stored.Trim();

        // NormalizePhone already resolves "+225…", "00225…" and the trunk-zero plans into a bare
        // digit string with the indicatif kept — reused rather than re-implemented so there is one
        // answer to "what does this number mean".
        var digits = SensitiveValueNormalizer.NormalizePhone(trimmed);

        if (digits.Length == 0) return null;

        // Whether the stored form declared its country. NormalizePhone drops the plus, so the
        // question has to be asked of the ORIGINAL value — this is the distinction the whole
        // method turns on.
        var wasInternational = trimmed.StartsWith('+')
                            || trimmed.StartsWith("00", StringComparison.Ordinal);

        if (!wasInternational)
        {
            if (string.IsNullOrWhiteSpace(defaultCallingCode)) return null;

            var code = new string([.. defaultCallingCode.Where(char.IsAsciiDigit)]);
            if (code.Length == 0) return null;

            // A locally-dialled number keeps its leading zero in the West African plans this
            // platform serves — Côte d'Ivoire's 0708091801 is ten significant digits — so the
            // code is simply prefixed. The countries that DO have a trunk zero are handled by
            // NormalizePhone, but only for values that arrived in international form; a local
            // number from one of those plans would keep a zero it should lose.
            //
            // Accepted knowingly: this platform's tenants are UEMOA institutions whose plans have
            // no trunk prefix, and the alternative — stripping a zero that is significant in Côte
            // d'Ivoire — corrupts the common case to fix the rare one. A deployment that needs the
            // other behaviour sets its customers' numbers in international form, which is already
            // what the importers encourage.
            digits = code + digits;
        }

        // A plausible E.164: 8 to 15 digits. Shorter is a truncated entry or a short code, longer
        // is not a telephone number — and either way a provider rejects it, at the cost of a
        // billed attempt.
        return digits.Length is >= 8 and <= 15 ? $"+{digits}" : null;
    }
}
