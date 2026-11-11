namespace Sankore.Shared.Kernel;

/// <summary>
/// The name a free-trial institution chooses for its address: <c>acme</c> in
/// <c>https://acme.talisoftcrm.com</c> (<c>docs/essai-gratuit-socle.md</c>). One definition, read
/// by the public sign-up route of Sankore.Api and by the registry that stores the request: two
/// copies would be two chances to accept a name the other refuses.
///
/// <para>
/// One DNS label, 3 to 30 characters, <c>a-z</c>, <c>0-9</c> and inner hyphens. It must also be an
/// origin the API's CORS suffix rule accepts (<c>CorsSetup.IsInstitutionSubdomain</c>) and a host
/// the Traefik router matches, which is why a dot or an accented letter is refused rather than
/// translated.
/// </para>
/// </summary>
public static class TrialSlug
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    public const string Invalid = "TRIAL_SLUG_INVALID";
    public const string Reserved = "TRIAL_SLUG_RESERVED";
    public const string Taken = "TRIAL_SLUG_TAKEN";

    /// <summary>
    /// Names that are, or may one day be, the platform's own hosts or words a visitor would read as
    /// the platform speaking. Refused whatever the registry holds.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "www", "api", "admin", "app", "apps", "mail", "smtp", "imap", "pop", "webmail", "mx", "ns", "ns1", "ns2",
        "crm", "talisoft", "talisoftcrm", "sankore", "support", "help", "aide", "docs", "doc", "status", "statut",
        "jobs", "hangfire", "grafana", "seq", "mq", "rabbitmq", "aspire", "ops", "monitoring", "metrics",
        "auth", "login", "connexion", "account", "compte", "billing", "facturation", "paiement", "payment",
        "demo", "test", "tests", "recette", "staging", "preprod", "dev", "sandbox", "essai", "trial", "beta",
        "ingest", "webhook", "webhooks", "cdn", "static", "assets", "media", "files", "sdk", "blog", "news",
        "plateforme", "platform", "security", "securite", "abuse", "contact", "info", "noreply", "no-reply",
    };

    /// <summary>Trimmed and lower-cased; null for a blank input.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// <see langword="null"/> when <paramref name="normalized"/> is an acceptable name, else
    /// <see cref="Invalid"/> or <see cref="Reserved"/>. Availability is the registry's to say.
    /// </summary>
    public static string? Check(string? normalized)
    {
        if (normalized is null
            || normalized.Length is < MinLength or > MaxLength
            || normalized[0] == '-' || normalized[^1] == '-'
            || normalized.Contains("--", StringComparison.Ordinal)
            || !normalized.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            return Invalid;

        return ReservedNames.Contains(normalized) ? Reserved : null;
    }
}
