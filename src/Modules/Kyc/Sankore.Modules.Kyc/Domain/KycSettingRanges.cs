namespace Sankore.Modules.Kyc.Domain;

using System.Globalization;

/// <summary>
/// The admissible range of every KYC parameter, beyond its declared type.
///
/// <para>
/// A type check is not enough the moment these values become writable over HTTP. <c>"int"</c>
/// accepts <c>0</c> and <c>-1</c>, and each of those has a meaning here that nobody wants: a flow
/// window of zero day measures nothing, an alert threshold of zero warns on every operation,
/// <c>face-match-max-attempts = 0</c> puts the branch manager on every low-risk file from the first
/// capture, and a review periodicity of zero year schedules a review that is always overdue. None
/// of it would be visible at the screen that saved it — it surfaces weeks later, in a nightly job or
/// in an approval circuit nobody expected to be there.
/// </para>
///
/// <para>
/// The ranges are wide on purpose: this is a guard against a value that cannot mean anything, not a
/// second opinion on the tenant's policy. A microfinance institution that wants a 10 000 ceiling or
/// a 10-year review is entitled to it.
/// </para>
/// </summary>
public static class KycSettingRanges
{
    /// <summary>
    /// Null when the value is acceptable for that key; otherwise a human-readable reason, suitable
    /// for a 400 payload. Returns null for an unknown key: whether a key exists is
    /// <see cref="KycSettingKeys.Find"/>'s decision, and the caller must answer
    /// <see cref="KycErrors.SettingUnknown"/> (404) rather than a range error.
    /// </summary>
    public static string? Validate(string key, string value)
    {
        var declared = KycSettingKeys.Find(key);
        if (declared is null) return null;

        return declared.Key switch
        {
            KycSettingKeys.SimplifiedMaxBalance or KycSettingKeys.SimplifiedMaxMonthlyFlow
                => Decimal(value, min: 1m, "Le plafond doit être un montant strictement positif."),

            KycSettingKeys.SimplifiedFlowWindowDays
                => Int(value, 1, 365, "La fenêtre de flux doit tenir entre 1 et 365 jours."),

            KycSettingKeys.SimplifiedAlertPct
                => Int(value, 1, 100, "Le seuil d'alerte est un pourcentage entre 1 et 100."),

            KycSettingKeys.CapsCurrency
                => Currency(value),

            KycSettingKeys.ReviewYearsHigh or KycSettingKeys.ReviewYearsStandard
                or KycSettingKeys.ReviewYearsLow
                => Int(value, 1, 30, "La périodicité de revue doit tenir entre 1 et 30 ans."),

            KycSettingKeys.ReviewGraceDays
                => Int(value, 0, 365, "Le délai de grâce doit tenir entre 0 et 365 jours."),

            // Zero would mean "the ceiling is reached before the first attempt", which adds the
            // branch manager to every file — the one clause that lifts a low-risk ladder.
            KycSettingKeys.FaceMatchMaxAttempts
                => Int(value, 1, 10, "Le nombre d'essais de comparaison faciale doit tenir entre 1 et 10."),

            KycSettingKeys.VerificationRejectionFloor
                => Int(value, 0, 100, "Le plancher de rejet est un score entre 0 et 100."),

            _ => null,
        };
    }

    private static string? Int(string value, int min, int max, string message)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
           && parsed >= min && parsed <= max
            ? null
            : message;

    private static string? Decimal(string value, decimal min, string message)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
           && parsed >= min
            ? null
            : message;

    /// <summary>
    /// ISO 4217 is three letters. Not checked against the real list — a closed list of currency
    /// codes in this module would be one more thing to maintain, and a wrong-but-well-formed code
    /// is visible on the first screen that renders an amount.
    /// </summary>
    private static string? Currency(string value)
        => value.Length == 3 && value.All(char.IsAsciiLetter)
            ? null
            : "La devise est un code ISO 4217 de trois lettres, par exemple XOF.";
}
