namespace Sankore.Modules.Integration.Features.Telephony;

/// <summary>
/// The shape a caller number must have before it is stored (TEL-B-05).
///
/// <para>
/// <b>E.164 and nothing else, which is a narrower rule than M01 applies to a customer's
/// number.</b> That asymmetry is deliberate: M01 stores what a prospect dictated at a counter,
/// because <c>SensitiveValueNormalizer.NormalizePhone</c> infers no indicatif and inferring one
/// wrongly makes two subscribers collide in a blind index. This number is not dictated — it is
/// the institution's own line, typed by an administrator into a settings screen, and it is handed
/// straight to a provider's API as the number to present. A provider refuses a local form, so a
/// value accepted here and refused there would surface as every one of that agent's calls failing
/// with an error nobody connects to a settings field.
/// </para>
///
/// <para>
/// Shape only. Whether the provider actually owns the number is the provider's answer, and it
/// gives it on the first call rather than on a validation this module could run.
/// </para>
/// </summary>
internal static class CallerNumber
{
    internal const string Message =
        "defaultCallerNumber must be in international E.164 form — a leading + followed by 8 to "
        + "15 digits, for example +2252021000000.";

    internal static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var trimmed = value.Trim();

        if (trimmed.Length is < 9 or > 16) return false;
        if (trimmed[0] != '+') return false;

        // Digits only after the plus: spaces and dashes are how a human writes a number and not
        // how an API takes one, and silently stripping them here would mean the value an
        // administrator reads back is not the value that was validated.
        for (var i = 1; i < trimmed.Length; i++)
        {
            if (!char.IsAsciiDigit(trimmed[i])) return false;
        }

        return true;
    }
}
