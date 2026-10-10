namespace Sankore.Modules.Leads.Domain;

using System.Text.Json.Serialization;

/// <summary>
/// Polymorphic base for mode-specific lead source settings.
/// Stored as JSONB with $mode discriminator for deserialization.
/// No field may carry a name evoking a secret (secret, password, token, apiKey)
/// — credentials live in the vault, referenced by opaque ID only.
/// </summary>
/// <remarks>
/// <para>
/// Polymorphic serialization handled by <c>SourceSettingsJsonConverter</c> (HTTP)
/// and <c>SourceSettingsConverter</c> (EF). The $mode discriminator is injected/inferred
/// by these converters — no [JsonPolymorphic] attributes needed.
/// </para>
///
/// <para>
/// <b>The STORED shape is the editor's shape</b> (schema v3). The settings screen is the only
/// author of this object, and it writes nested blocks — <c>consent</c>, <c>script</c>,
/// <c>hostedForm</c>, <c>pull</c> — plus <c>fieldMappings</c> for every mode. These records used
/// to declare a flat shape of their own that nothing wrote: System.Text.Json drops unmapped
/// members, so a tenant configuring a captcha, a hosted form, a consent policy or a whole
/// provider API saw "Enregistré" and lost every field, with no error on either side. Matching
/// the editor is what makes the round trip real; <c>SourceSettingsUpgrader</c> moves rows written
/// in the old flat shape.
/// </para>
///
/// <para>
/// Two kinds of property are therefore NOT in the editor's blocks, deliberately:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Server-owned fields</b> stay flat on the record — the outbound safety caps
/// (<c>TimeoutSeconds</c>, <c>MaxResponseBytes</c>, <c>MaxPagesPerRun</c>), the vault
/// references, <c>RequestBodyTemplate</c>, <c>ExtraHeaders</c>, the HMAC signature
/// configuration. The editor never sends them, so nesting them under a block it rewrites
/// wholesale would delete them on the next save. These are the limits that keep a pull from
/// hammering someone's API and the webhook from accepting an unsigned body: they are not the
/// UI's to own.
/// </item>
/// <item>
/// <b>Projections</b> (<c>[JsonIgnore]</c>, get-only) give the server the flat names and the
/// folded shapes it actually consumes — <c>EndpointUrl</c> composed from
/// <c>Pull.BaseUrl</c> + <c>RequestPath</c> + <c>RequestParams</c>, <c>AuthType</c> folded from
/// the editor's credential type and header location, <c>HoneypotFieldName</c> resolved from a
/// boolean. The correspondence lives here, once, instead of being re-derived in the puller, the
/// web-ingest endpoint and the provider doc.
/// </item>
/// </list>
/// </remarks>
public abstract record SourceSettings
{
    /// <summary>Schema version for forward-compatible upgrades.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Returns the expected IntegrationMode for this settings type.</summary>
    public abstract IntegrationMode ExpectedMode { get; }

    /// <summary>
    /// Mapping rules: inbound payload field → Lead field.
    ///
    /// <para>
    /// On the BASE, because the mapping editor saves the same way whatever the mode. It used to
    /// be declared per-subtype and omitted from <c>EmbeddedScriptSettings</c>, so a mapping
    /// configured on a web-form source was accepted and dropped while the identical screen
    /// persisted it for a webhook.
    /// </para>
    /// </summary>
    public IReadOnlyList<FieldMappingRule>? FieldMappings { get; init; }

    /// <summary>Consent policy (FE-08). Common to every mode.</summary>
    public ConsentConfig? Consent { get; init; }

    /// <summary>
    /// Product this source feeds by default, when no mapping rule supplies one.
    ///
    /// <para>
    /// Here rather than a column on <c>LeadSourceConfig</c> because that is where the editor
    /// puts it and it has no column to go to yet — the creation screen says as much in its own
    /// comment. Move it to a column and this property goes with it.
    /// </para>
    /// </summary>
    public string? DefaultProductCode { get; init; }
}

// ───────────────────────────────────────────────────────────────────────────
//  Editor blocks
// ───────────────────────────────────────────────────────────────────────────

/// <summary>How consent is obtained for leads from this source (FE-08).</summary>
public enum ConsentPolicy
{
    None,
    CollectedByForm,
    ProviderAttested,
    LegitimateInterest
}

/// <summary>Consent configuration. Common to every mode.</summary>
public sealed record ConsentConfig
{
    public ConsentPolicy Policy { get; init; } = ConsentPolicy.None;

    /// <summary>JSONPath to the consent flag in the inbound payload.</summary>
    public string? ConsentFieldPath { get; init; }

    /// <summary>Version of the consent wording in force when the lead was captured.</summary>
    public string? ConsentTextVersion { get; init; }

    /// <summary>Reference of the provider contract, for <see cref="ConsentPolicy.ProviderAttested"/>.</summary>
    public string? ProviderContractRef { get; init; }
}

/// <summary>
/// Whether the snippet binds a form already on the page or renders the hosted one.
/// Wire values are the editor's (<c>existing</c> / <c>hosted</c>), pinned: the SDK and the
/// editor's select both compare the raw string, and PostgreSQL string equality is
/// case-sensitive.
/// </summary>
public enum ScriptFormMode
{
    [JsonStringEnumMemberName("existing")]
    Existing,

    [JsonStringEnumMemberName("hosted")]
    Hosted
}

/// <summary>What the snippet does after a successful submit.</summary>
public enum ScriptAfterSubmit
{
    [JsonStringEnumMemberName("message")]
    Message,

    [JsonStringEnumMemberName("redirect")]
    Redirect
}

/// <summary>Captcha provider guarding the public web ingest.</summary>
public enum CaptchaProvider
{
    None,
    Turnstile,
    HCaptcha,
    RecaptchaV3
}

/// <summary>Embedded-script configuration (FE-10).</summary>
public sealed record ScriptConfig
{
    /// <summary>Allowed origin domains for CORS validation.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public ScriptFormMode FormMode { get; init; } = ScriptFormMode.Existing;

    /// <summary>CSS selector or DOM container ID the snippet binds to.</summary>
    public string? FormSelector { get; init; }

    /// <summary>`name` attributes of the existing form, comma-separated.</summary>
    public string? FormFieldNames { get; init; }

    public CaptchaProvider CaptchaProvider { get; init; } = CaptchaProvider.None;

    /// <summary>Public site key. Not a secret: it is served to the browser.</summary>
    public string? CaptchaSiteKey { get; init; }

    /// <summary>Whether a honeypot field is planted in the form.</summary>
    public bool Honeypot { get; init; } = true;

    /// <summary>Minimum seconds between form load and submit (bot detection). 0 = disabled.</summary>
    public int MinFillTimeSeconds { get; init; } = 3;

    public ScriptAfterSubmit AfterSubmit { get; init; } = ScriptAfterSubmit.Message;

    public string? SuccessMessage { get; init; }

    public string? RedirectUrl { get; init; }

    public bool PreventDefaultSubmit { get; init; } = true;
}

/// <summary>Input type of a hosted-form field. Wire values are the editor's and the SDK's.</summary>
public enum HostedFieldType
{
    [JsonStringEnumMemberName("text")]
    Text,

    [JsonStringEnumMemberName("email")]
    Email,

    [JsonStringEnumMemberName("tel")]
    Tel,

    [JsonStringEnumMemberName("textarea")]
    Textarea,

    [JsonStringEnumMemberName("select")]
    Select
}

/// <summary>One field of the form rendered by the SDK (FE-16).</summary>
public sealed record HostedFormField
{
    /// <summary>Attribute `name` posted to the CRM — the mapping's source field.</summary>
    public string Name { get; init; } = default!;

    public string Label { get; init; } = default!;

    public HostedFieldType Type { get; init; } = HostedFieldType.Text;

    public bool IsRequired { get; init; }

    public string? Placeholder { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>Definition of the form the SDK renders when <c>FormMode</c> is Hosted (FE-15/FE-16).</summary>
public sealed record HostedFormConfig
{
    public IReadOnlyList<HostedFormField> Fields { get; init; } = [];

    /// <summary>Consent text displayed below the form (e.g. GDPR notice).</summary>
    public string? ConsentText { get; init; }

    /// <summary>Consent text version, for tracking changes.</summary>
    public string? ConsentVersion { get; init; }

    public string? SubmitLabel { get; init; }

    /// <summary>Accent colour, exposed to the host site as a CSS variable.</summary>
    public string? AccentColor { get; init; }

    public string? FontFamily { get; init; }

    /// <summary>
    /// Visual theme served to the SDK, which distinguishes only <c>"dark"</c> from everything
    /// else. The editor has no field for it yet and the two it does have —
    /// <see cref="AccentColor"/> and <see cref="FontFamily"/> — do not reach the form endpoint
    /// at all; wiring them there means changing <c>WebFormResponse</c> and the deployed SDK
    /// together, which is its own piece of work. Null therefore keeps exactly today's
    /// behaviour (light), rather than this field being dropped and the response losing it.
    /// </summary>
    public string? Theme { get; init; }
}

/// <summary>Credential family the provider API expects, as the editor offers it.</summary>
public enum PullCredentialType
{
    None,
    ApiKey,
    Bearer,
    Basic,
    OAuthClientCredentials
}

/// <summary>Where an API key travels. Only meaningful for <see cref="PullCredentialType.ApiKey"/>.</summary>
public enum PullAuthLocation
{
    [JsonStringEnumMemberName("header")]
    Header,

    [JsonStringEnumMemberName("query")]
    Query
}

/// <summary>HTTP verb used to fetch. Wire values upper-case, as the editor's select holds them.</summary>
public enum PullHttpMethod
{
    [JsonStringEnumMemberName("GET")]
    Get,

    [JsonStringEnumMemberName("POST")]
    Post
}

/// <summary>Verb used to acknowledge a fetched batch back to the provider.</summary>
public enum AckHttpMethod
{
    [JsonStringEnumMemberName("POST")]
    Post,

    [JsonStringEnumMemberName("PUT")]
    Put,

    [JsonStringEnumMemberName("PATCH")]
    Patch
}

/// <summary>
/// Provider-API configuration as the editor saves it (FE-19/FE-21).
///
/// <para>
/// The editor splits what the puller consumes as one value: the URL is three fields here
/// (<see cref="BaseUrl"/>, <see cref="RequestPath"/>, <see cref="RequestParams"/>) and the
/// credential is two (<see cref="AuthType"/> plus <see cref="AuthHeaderLocation"/>). The folding
/// lives in <c>ScheduledPullSettings</c>'s projections, not here and not in the puller.
/// </para>
/// </summary>
public sealed record PullConfig
{
    public string? BaseUrl { get; init; }

    public PullCredentialType AuthType { get; init; } = PullCredentialType.None;

    /// <summary>Header or query-parameter name carrying the API key.</summary>
    public string? AuthHeaderName { get; init; }

    public PullAuthLocation AuthHeaderLocation { get; init; } = PullAuthLocation.Header;

    /// <summary>Basic-auth user name. The password is a vault secret, never here.</summary>
    public string? BasicUsername { get; init; }

    // Oauth, not OAuth: JsonNamingPolicy.CamelCase turns "OAuthTokenUrl" into
    // "oAuthTokenUrl" — it stops at the uppercase letter before a lowercase one — while the
    // editor writes "oauthTokenUrl". Reads tolerate either (case-insensitive), so only the
    // round TRIP broke: the three OAuth fields came back under a key the editor does not read
    // and its form showed them empty every time it reopened.
    public string? OauthTokenUrl { get; init; }

    public string? OauthClientId { get; init; }

    public string? OauthScope { get; init; }

    public PullHttpMethod RequestMethod { get; init; } = PullHttpMethod.Get;

    /// <summary>Path appended to <see cref="BaseUrl"/>, e.g. <c>/leads</c>.</summary>
    public string? RequestPath { get; init; }

    /// <summary>Query string appended to the path. Supports the URL template variables.</summary>
    public string? RequestParams { get; init; }

    public PullPaginationStrategy PaginationStrategy { get; init; } = PullPaginationStrategy.None;

    public string? PageParamName { get; init; }

    public string? PageSizeParamName { get; init; }

    /// <summary>JSONPath to the cursor / next token.</summary>
    public string? CursorJsonPath { get; init; }

    /// <summary>Query parameter carrying the incremental watermark.</summary>
    public string? SinceField { get; init; }

    /// <summary>JSONPath to the items array, e.g. <c>$.data</c>.</summary>
    public string? DataJsonPath { get; init; }

    /// <summary>JSONPath to the external id of each item.</summary>
    public string? IdJsonPath { get; init; }

    /// <summary>JSONPath to each item's creation date.</summary>
    public string? DateJsonPath { get; init; }

    public string? CronExpression { get; init; }

    /// <summary>Preset the editor's schedule picker was left on. UI state, kept for the round trip.</summary>
    public string? SchedulePreset { get; init; }

    /// <summary>Hour of the daily pull the preset stands for. UI state, like <see cref="SchedulePreset"/>.</summary>
    public int DailyHour { get; init; }

    public bool AckEnabled { get; init; }

    public AckHttpMethod AckMethod { get; init; } = AckHttpMethod.Post;

    public string? AckPath { get; init; }

    /// <summary>
    /// Cost per lead as typed in this screen. A COPY: the authoritative value is
    /// <c>LeadSourceConfig.CostPerLead</c>, which the same request also sets and which
    /// <c>Lead.AcquisitionCost</c> snapshots. Kept so the screen reads back what was typed;
    /// nothing server-side bills against it.
    /// </summary>
    public decimal? CostPerLead { get; init; }

    public string? CostCurrency { get; init; }
}

// ───────────────────────────────────────────────────────────────────────────
//  Per-mode settings
// ───────────────────────────────────────────────────────────────────────────

/// <summary>Settings for EmbeddedScript mode (JS snippet on a website).</summary>
public sealed record EmbeddedScriptSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.EmbeddedScript;

    /// <summary>Snippet behaviour, as the editor saves it.</summary>
    public ScriptConfig? Script { get; init; }

    /// <summary>The hosted form's definition. Null when the snippet binds an existing form.</summary>
    public HostedFormConfig? HostedForm { get; init; }

    // ── Projections for the public ingest path ──────────────────────────

    /// <summary>Allowed origins, for the CORS and ping checks.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllowedOrigins => Script?.AllowedOrigins ?? [];

    /// <summary>Selector the snippet binds to. The default matches the SDK's own.</summary>
    [JsonIgnore]
    public string? FormContainerId => Script?.FormSelector;

    [JsonIgnore]
    public string? RedirectUrl
        => Script?.AfterSubmit == ScriptAfterSubmit.Redirect ? Script.RedirectUrl : null;

    /// <summary>
    /// Provider name for <c>ICaptchaValidator</c>, or null when no captcha guards the form.
    ///
    /// <para>
    /// <c>None</c> folds to null on purpose: the ingest endpoint treats "not null" as "validate",
    /// so an editor that stores the string "None" would make every submission fail a captcha the
    /// tenant deliberately turned off.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? CaptchaProviderName
        => Script is null || Script.CaptchaProvider == CaptchaProvider.None
            ? null
            : Script.CaptchaProvider.ToString();

    /// <summary>
    /// Minimum seconds between form load and submit. Falls back to the protective default, not
    /// to zero: the ingest endpoint reads zero as "no delay check", so a source whose script
    /// block is absent would silently lose its anti-bot delay.
    /// </summary>
    [JsonIgnore]
    public int MinSubmitDelaySeconds => Script?.MinFillTimeSeconds ?? DefaultMinSubmitDelaySeconds;

    /// <summary>The figure this record carried as its own default before the block existed.</summary>
    public const int DefaultMinSubmitDelaySeconds = 3;

    /// <summary>
    /// Name of the honeypot field, or null when none is planted. The editor stores a boolean;
    /// the field's name is ours to choose and must match what the SDK plants.
    /// </summary>
    [JsonIgnore]
    public string? HoneypotFieldName => Script?.Honeypot == true ? DefaultHoneypotFieldName : null;

    /// <summary>The name the SDK plants, and therefore the one the ingest endpoint reads.</summary>
    public const string DefaultHoneypotFieldName = "_sankore_hp";

    /// <summary>Fields served by <c>GET ingest/web/{publicKey}/form</c>, in editor order.</summary>
    [JsonIgnore]
    public IReadOnlyList<FormFieldDefinition>? FormFields
        => HostedForm?.Fields is not { Count: > 0 } fields
            ? null
            : [.. fields.Select((f, i) => new FormFieldDefinition
            {
                Name = f.Name,
                Label = f.Label,
                Type = f.Type.ToString().ToLowerInvariant(),
                IsRequired = f.IsRequired,
                Placeholder = f.Placeholder,
                Options = f.Options,
                Order = i + 1,
            })];

    [JsonIgnore]
    public string? ConsentText => HostedForm?.ConsentText;

    [JsonIgnore]
    public string? ConsentVersion => HostedForm?.ConsentVersion;

    [JsonIgnore]
    public string? SubmitButtonLabel => HostedForm?.SubmitLabel;

    [JsonIgnore]
    public string? Theme => HostedForm?.Theme;

    /// <summary>
    /// Accent colour served to the SDK, or null when the tenant left the default.
    ///
    /// <para>
    /// Rejected rather than passed through when it is not a plain CSS colour: the SDK
    /// interpolates this into a <c>&lt;style&gt;</c> element, so a value carrying <c>}</c> would
    /// close the rule and let whatever follows style the host page. The validators refuse such a
    /// value on the way in; this is the second gate, because the column predates them and a row
    /// written by hand would otherwise reach a customer's site.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? AccentColor => CssColor.SanitizeOrNull(HostedForm?.AccentColor);

    /// <summary>Font stack served to the SDK. Sanitised like <see cref="AccentColor"/>.</summary>
    [JsonIgnore]
    public string? FontFamily => CssColor.SanitizeFontFamilyOrNull(HostedForm?.FontFamily);
}

/// <summary>
/// One field served to the SDK by the public form endpoint.
///
/// <para>
/// Kept as the WIRE shape of that endpoint rather than merged into
/// <see cref="HostedFormField"/>: it carries <c>Order</c>, which the editor expresses as array
/// position, and its <c>Type</c> is a plain string because the SDK reads it straight into a DOM
/// input type.
/// </para>
/// </summary>
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

    /// <summary>
    /// IP addresses allowed to push webhooks. Empty = any IP allowed.
    ///
    /// <para>
    /// Named as the editor names it. It was <c>AllowedIpAddresses</c> while the editor wrote
    /// <c>allowedIps</c>, so the allow-list a tenant entered was dropped on save and the endpoint
    /// went on accepting pushes from anywhere — the one failure here that is a security control
    /// silently turning itself off.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedIps { get; init; } = [];

    /// <summary>JSONPath to extract the external ID from each payload item.</summary>
    public string? ExternalIdPath { get; init; }

    // ── Server-owned: the editor never sends these ──────────────────────

    /// <summary>Expected Content-Type (default: application/json).</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>HMAC algorithm for signature validation (e.g. "sha256").</summary>
    public string? SignatureAlgorithm { get; init; }

    /// <summary>Header name carrying the HMAC signature.</summary>
    public string? SignatureHeaderName { get; init; }

    /// <summary>Vault reference for the HMAC signing credential (never inline).</summary>
    public string? SignatureCredentialVaultRef { get; init; }
}

/// <summary>Settings for ScheduledPull mode (periodic fetch from external API).</summary>
public sealed record ScheduledPullSettings : SourceSettings
{
    public override IntegrationMode ExpectedMode => IntegrationMode.ScheduledPull;

    /// <summary>Provider API configuration, as the editor saves it.</summary>
    public PullConfig? Pull { get; init; }

    // ── Server-owned: the editor never sends these ──────────────────────

    /// <summary>Vault reference for the credential (API key, token, password, client secret).</summary>
    public string? AuthCredentialVaultRef { get; init; }

    /// <summary>JSONPath to total count (Page/Offset).</summary>
    public string? TotalCountPath { get; init; }

    public int PageSize { get; init; } = 100;

    /// <summary>Max pages per run (safety). Default: 50.</summary>
    public int MaxPagesPerRun { get; init; } = 50;

    /// <summary>Request timeout seconds (max 60).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Max response size bytes (max 5 MB).</summary>
    public int MaxResponseBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>POST body template. Supports {{since}}, {{cursor}}, etc.</summary>
    public string? RequestBodyTemplate { get; init; }

    /// <summary>Extra static headers.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    // ── Projections for the puller and the orchestrator ─────────────────

    /// <summary>
    /// URL template the puller resolves, composed from the editor's three fields. Supports
    /// {{since}}, {{cursor}}, {{page}}, {{pageSize}}, {{offset}}.
    /// </summary>
    [JsonIgnore]
    public string EndpointUrl => ComposeEndpointUrl();

    [JsonIgnore]
    public string HttpMethod => (Pull?.RequestMethod ?? PullHttpMethod.Get) switch
    {
        PullHttpMethod.Post => "POST",
        _ => "GET",
    };

    /// <summary>
    /// Cron expression for the pull schedule. Falls back to every six hours rather than to
    /// empty: <c>CrontabSchedule.Parse</c> throws on a blank expression, and the orchestrator
    /// parses this once per minute for every active source.
    /// </summary>
    [JsonIgnore]
    public string CronSchedule
        => string.IsNullOrWhiteSpace(Pull?.CronExpression) ? DefaultCronSchedule : Pull.CronExpression;

    /// <summary>Four times a day, the figure this record carried as its own default.</summary>
    public const string DefaultCronSchedule = "0 */6 * * *";

    /// <summary>
    /// The credential family as the puller switches on it: the editor's <c>ApiKey</c> folds with
    /// its header/query location into the two members the puller distinguishes.
    /// </summary>
    [JsonIgnore]
    public PullAuthType AuthType => (Pull?.AuthType ?? PullCredentialType.None) switch
    {
        PullCredentialType.ApiKey when Pull!.AuthHeaderLocation == PullAuthLocation.Query
            => PullAuthType.ApiKeyQuery,
        PullCredentialType.ApiKey => PullAuthType.ApiKeyHeader,
        PullCredentialType.Bearer => PullAuthType.Bearer,
        PullCredentialType.Basic => PullAuthType.Basic,
        PullCredentialType.OAuthClientCredentials => PullAuthType.OAuthClientCredentials,
        _ => PullAuthType.None,
    };

    /// <summary>Header name for ApiKeyHeader (e.g. "X-Api-Key"). Null when the key travels in the query.</summary>
    [JsonIgnore]
    public string? AuthHeaderName
        => Pull?.AuthHeaderLocation == PullAuthLocation.Header ? Pull.AuthHeaderName : null;

    /// <summary>Query param name for ApiKeyQuery (e.g. "api_key").</summary>
    [JsonIgnore]
    public string? AuthQueryParamName
        => Pull?.AuthHeaderLocation == PullAuthLocation.Query ? Pull.AuthHeaderName : null;

    [JsonIgnore]
    public string? BasicAuthUsername => Pull?.BasicUsername;

    [JsonIgnore]
    public string? OAuthTokenUrl => Pull?.OauthTokenUrl;

    [JsonIgnore]
    public string? OAuthClientId => Pull?.OauthClientId;

    [JsonIgnore]
    public string? OAuthScopes => Pull?.OauthScope;

    /// <summary>JSONPath to the items array (e.g. "$.data").</summary>
    [JsonIgnore]
    public string? ItemsPath => Pull?.DataJsonPath;

    /// <summary>JSONPath to extract external ID per item.</summary>
    [JsonIgnore]
    public string? ExternalIdPath => Pull?.IdJsonPath;

    [JsonIgnore]
    public PullPaginationStrategy Pagination
        => Pull?.PaginationStrategy ?? PullPaginationStrategy.None;

    /// <summary>JSONPath to cursor/next token (Cursor strategy).</summary>
    [JsonIgnore]
    public string? CursorPath => Pull?.CursorJsonPath;

    /// <summary>
    /// Joins base URL, path and query the way a person would read them back, tolerating a
    /// trailing slash on the base and a missing leading slash on the path — the editor accepts
    /// both and a double slash changes the route on plenty of gateways.
    /// </summary>
    private string ComposeEndpointUrl()
    {
        var baseUrl = (Pull?.BaseUrl ?? string.Empty).Trim();
        var path = (Pull?.RequestPath ?? string.Empty).Trim();
        var query = (Pull?.RequestParams ?? string.Empty).Trim().TrimStart('?', '&');

        var url = (baseUrl.Length, path.Length) switch
        {
            (0, _) => path,
            (_, 0) => baseUrl,
            _ => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}",
        };

        if (query.Length == 0) return url;

        return url.Contains('?', StringComparison.Ordinal) ? $"{url}&{query}" : $"{url}?{query}";
    }
}

public enum PullAuthType
{
    None,
    ApiKeyHeader,
    ApiKeyQuery,
    Bearer,
    Basic,
    OAuthClientCredentials
}

public enum PullPaginationStrategy
{
    None,
    Page,
    Offset,
    Cursor,
    LinkHeader,
    Since
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
