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

            // Zero would accept a proof of address issued today and nothing else, which no
            // counter can satisfy; a year is already far beyond what article 13.6 allows.
            KycSettingKeys.ProofOfAddressMaxAgeDays
                => Int(value, 1, 365,
                    "L'ancienneté maximale du justificatif de domicile doit tenir entre 1 et 365 jours."),

            // Zero is allowed: an institution whose mail provider is reliably ordered is entitled
            // to send both messages at once. The ceiling is a day — a link the customer receives
            // the next morning is one they no longer connect to the conversation they just had
            // with their agent.
            KycSettingKeys.RemoteEmailDelayMinutes
                => Int(value, 0, 1440,
                    "Le délai d'envoi du lien doit tenir entre 0 et 1440 minutes (24 h)."),

            // remote-otp has no RANGE — a bool has two values and the type check covers both.
            // What it does have is an availability guard, in UpdateKycSettingHandler: this
            // deployment has no SMS channel, so turning it on would lock every customer out. A
            // range validator cannot see the container, which is why the guard is not here.

            // One hour is already an aggressive chase on a file a customer sent at nine in the
            // evening; a fortnight is already far past the point where the relance is the
            // problem. Zero is excluded because it would relance every file on the next sweep.
            KycSettingKeys.RemoteReviewSlaHours
                => Int(value, 1, 336,
                    "Le délai de relance doit tenir entre 1 et 336 heures (14 jours)."),

            // Five minutes is already short for photographing a card; two hours is already long
            // for a bearer session on a phone that may be shared or lost.
            KycSettingKeys.RemoteSessionMinutes
                => Int(value, 5, 120, "La durée de session doit tenir entre 5 et 120 minutes."),

            // Zero would lock the link before the customer typed anything; a hundred is no limit
            // at all against a date-of-birth guess.
            KycSettingKeys.RemoteMaxIdentifyAttempts
                => Int(value, 3, 10,
                    "Le nombre d'essais d'identification doit tenir entre 3 et 10."),

            // Zero would mean the first blurred photo ends the journey.
            KycSettingKeys.RemoteMaxQualityRetries
                => Int(value, 1, 20,
                    "Le nombre de reprises de photo doit tenir entre 1 et 20."),

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
