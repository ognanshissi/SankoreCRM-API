namespace Sankore.Shared.Kernel;

/// <summary>
/// The one rule for storing a language code.
///
/// It lives in the Kernel because four places need to agree on it — the email template renderer,
/// the Client aggregate, AppUser and UserProfile — and they did not. A client whose language read
/// "FR" resolved no email template: template locales are stored lower-case and the lookup runs in
/// PostgreSQL, where string equality is case-sensitive, so that client was mailed the message's
/// own JSON payload as its body. The same value written four ways is exactly how that happens.
/// </summary>
public static class LanguageCode
{
    /// <summary>System default; every email template key is seeded in it.</summary>
    public const string Default = "fr";

    /// <summary>
    /// Lower-cases the code and keeps only its primary subtag, so "FR", "Fr", "fr-FR" and "fr_FR"
    /// become one stored value. Blank yields <see cref="Default"/>.
    /// </summary>
    public static string Normalize(string? value)
        => NormalizeOrNull(value) ?? Default;

    /// <summary>
    /// Same rule, but blank stays <c>null</c> — for a field where "no preference" is meaningful
    /// and must not be silently turned into French.
    /// </summary>
    public static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var primary = value.Trim().Split('-', '_')[0];

        return primary.Length == 0 ? null : primary.ToLowerInvariant();
    }
}
