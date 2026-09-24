namespace Sankore.Modules.Leads.Domain;

using System.Text.Json.Serialization;

/// <summary>
/// Polymorphic base for mode-specific lead source settings.
/// Stored as JSONB with $mode discriminator for deserialization.
/// No field may carry a name evoking a secret (secret, password, token, apiKey)
/// — credentials live in the vault, referenced by opaque ID only.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$mode")]
[JsonDerivedType(typeof(EmbeddedScriptSettings), "EmbeddedScript")]
[JsonDerivedType(typeof(ServerWebhookSettings), "ServerWebhook")]
[JsonDerivedType(typeof(ScheduledPullSettings), "ScheduledPull")]
[JsonDerivedType(typeof(PlatformSettings), "PlatformConnection")]
[JsonDerivedType(typeof(SocialTrackingSettings), "SocialTracking")]
[JsonDerivedType(typeof(InternalSettings), "Internal")]
public abstract record SourceSettings
{
    /// <summary>Schema version for forward-compatible upgrades.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Returns the expected IntegrationMode for this settings type.</summary>
    public abstract IntegrationMode ExpectedMode { get; }
}

/// <summary>Settings for EmbeddedScript mode (JS snippet on a website).</summary>
public sealed record EmbeddedScriptSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.EmbeddedScript;

    /// <summary>Allowed origin domains for CORS validation.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>CSS selector or DOM container ID where the form renders.</summary>
    public string? FormContainerId { get; init; }

    /// <summary>Optional redirect URL after submission.</summary>
    public string? RedirectUrl { get; init; }

    /// <summary>Captcha provider: "recaptcha", "turnstile", or null (disabled).</summary>
    public string? CaptchaProvider { get; init; }

    /// <summary>Minimum seconds between form load and submit (bot detection). 0 = disabled.</summary>
    public int MinSubmitDelaySeconds { get; init; } = 3;

    /// <summary>Name of the honeypot field (hidden, must be empty). Null = no honeypot.</summary>
    public string? HoneypotFieldName { get; init; }

    /// <summary>Form field definitions shown to the visitor.</summary>
    public IReadOnlyList<FormFieldDefinition>? FormFields { get; init; }

    /// <summary>Consent text displayed below the form (e.g. GDPR notice).</summary>
    public string? ConsentText { get; init; }

    /// <summary>Consent text version for tracking changes.</summary>
    public string? ConsentVersion { get; init; }

    /// <summary>Visual theme name (e.g. "light", "dark", "branded").</summary>
    public string? Theme { get; init; }

    /// <summary>Submit button label.</summary>
    public string? SubmitButtonLabel { get; init; }
}

/// <summary>Defines a single form field for the hosted web form.</summary>
public sealed record FormFieldDefinition
{
    /// <summary>Internal field name mapped to Lead property (e.g. "fullName", "phoneNumber").</summary>
    public string Name { get; init; } = default!;

    /// <summary>Display label shown to the visitor.</summary>
    public string Label { get; init; } = default!;

    /// <summary>Input type: text, email, tel, select, textarea, checkbox.</summary>
    public string Type { get; init; } = "text";

    /// <summary>Whether the field is required.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Placeholder text.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Options for select fields.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Display order (1-based).</summary>
    public int Order { get; init; }
}

/// <summary>Settings for ServerWebhook mode (inbound HTTP POST).</summary>
public sealed record ServerWebhookSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.ServerWebhook;

    /// <summary>Expected Content-Type (default: application/json).</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>HMAC algorithm for signature validation (e.g. "sha256").</summary>
    public string? SignatureAlgorithm { get; init; }

    /// <summary>Header name carrying the HMAC signature.</summary>
    public string? SignatureHeaderName { get; init; }

    /// <summary>Vault reference for the HMAC signing credential (never inline).</summary>
    public string? SignatureCredentialVaultRef { get; init; }

    /// <summary>JSON path mapping: external field → Lead field.</summary>
    public IReadOnlyDictionary<string, string>? FieldMapping { get; init; }

    /// <summary>IP addresses allowed to push webhooks. Empty = any IP allowed.</summary>
    public IReadOnlyList<string> AllowedIpAddresses { get; init; } = [];

    /// <summary>JSONPath to extract the external ID from each payload item.</summary>
    public string? ExternalIdPath { get; init; }
}

/// <summary>Settings for ScheduledPull mode (periodic fetch from external API).</summary>
public sealed record ScheduledPullSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.ScheduledPull;

    /// <summary>External API endpoint to poll.</summary>
    public string EndpointUrl { get; init; } = string.Empty;

    /// <summary>HTTP method (GET or POST).</summary>
    public string HttpMethod { get; init; } = "GET";

    /// <summary>Cron expression for the pull schedule.</summary>
    public string CronSchedule { get; init; } = "0 */6 * * *";

    /// <summary>Vault reference for the authentication credential.</summary>
    public string? AuthCredentialVaultRef { get; init; }

    /// <summary>JSON path to the array of leads in the response.</summary>
    public string? ResponseLeadsPath { get; init; }

    /// <summary>Field mapping: external → Lead.</summary>
    public IReadOnlyDictionary<string, string>? FieldMapping { get; init; }
}

/// <summary>Settings for PlatformConnection mode (Facebook, Instagram, LinkedIn, WhatsApp).</summary>
public sealed record PlatformSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.PlatformConnection;

    /// <summary>Platform identifier (e.g. "facebook", "instagram", "linkedin", "whatsapp").</summary>
    public string PlatformName { get; init; } = string.Empty;

    /// <summary>External page/account ID on the platform.</summary>
    public string? PageId { get; init; }

    /// <summary>External form ID (Facebook Lead Ads form, LinkedIn Lead Gen form).</summary>
    public string? FormId { get; init; }

    /// <summary>Vault reference for the platform OAuth credential.</summary>
    public string? OAuthCredentialVaultRef { get; init; }

    /// <summary>Field mapping: platform field → Lead field.</summary>
    public IReadOnlyDictionary<string, string>? FieldMapping { get; init; }
}

/// <summary>Settings for SocialTracking mode (social engagement tracking).</summary>
public sealed record SocialTrackingSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.SocialTracking;

    /// <summary>Social platform (e.g. "facebook", "twitter", "linkedin").</summary>
    public string PlatformName { get; init; } = string.Empty;

    /// <summary>Hashtags or keywords to track.</summary>
    public IReadOnlyList<string> TrackedKeywords { get; init; } = [];

    /// <summary>Minimum engagement score to create a lead.</summary>
    public int MinEngagementScore { get; init; }
}

/// <summary>Settings for Internal mode (manual capture, walk-in, referral, file import).</summary>
public sealed record InternalSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.Internal;

    /// <summary>Whether leads from this source require immediate qualification.</summary>
    public bool RequireImmediateQualification { get; init; }

    /// <summary>Default priority assigned to leads from this source.</summary>
    public string? DefaultPriority { get; init; }
}
