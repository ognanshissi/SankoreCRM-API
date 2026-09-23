namespace Sankore.Modules.Leads.Domain;

using System.Security.Cryptography;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Unified lead source configuration (F13.37).
/// Two classification axes: ChannelType (reporting) + IntegrationMode (technical).
/// </summary>
public sealed class LeadSourceConfig : ITenant
{
    private static readonly HashSet<IntegrationMode> PublicKeyModes =
        [IntegrationMode.EmbeddedScript, IntegrationMode.ServerWebhook];

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Unique machine-readable code per tenant.</summary>
    public string Code { get; private set; } = default!;

    /// <summary>Human-readable display label.</summary>
    public string Label { get; private set; } = default!;

    /// <summary>Optional description.</summary>
    public string? Description { get; private set; }

    /// <summary>Commercial channel — where the lead comes from (reporting/attribution).</summary>
    public LeadChannelType ChannelType { get; private set; }

    /// <summary>Technical integration — how the lead arrives (connector resolution via keyed DI).</summary>
    public IntegrationMode Mode { get; private set; }

    /// <summary>Draft / Active / Paused / Disabled lifecycle.</summary>
    public LeadSourceStatus Status { get; private set; }

    /// <summary>API key / webhook secret for push sources. Unique across all tenants.</summary>
    public string? PublicKey { get; private set; }

    /// <summary>Strongly typed, mode-specific settings. Stored as JSONB with $mode discriminator.</summary>
    public SourceSettings? Settings { get; private set; }

    /// <summary>Opaque reference to an external platform connection.</summary>
    public string? PlatformConnectionId { get; private set; }

    /// <summary>Dedup window in days against same source. 0 = no dedup. Default: 30.</summary>
    public int DedupWindowDays { get; private set; }

    /// <summary>Cost per lead for ROI tracking.</summary>
    public Money? CostPerLead { get; private set; }

    public bool IsSystem { get; private set; }
    public int DisplayOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>PostgreSQL xmin concurrency token for optimistic locking.</summary>
    public uint Version { get; private set; }

    private LeadSourceConfig() { }

    /// <summary>Generates a 32-byte base64url public key for push sources.</summary>
    public static string GeneratePublicKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static LeadSourceConfig Create(
        Guid tenantId,
        string code,
        string label,
        LeadChannelType channelType,
        int displayOrder,
        IntegrationMode? integrationMode = null,
        string? description = null,
        string? publicKey = null,
        SourceSettings? settings = null,
        string? platformConnectionId = null,
        int dedupWindowDays = 30,
        Money? costPerLead = null,
        bool isSystem = false)
    {
        var mode = integrationMode ?? ChannelModeMap.GetDefaultMode(channelType)
            ?? throw new DomainException($"No default mode for channel: {channelType}");

        if (!ChannelModeMap.IsModeAllowed(channelType, mode))
            throw new DomainException($"ModeNotAllowedForChannel: {mode} is not valid for {channelType}");

        if (settings is not null && settings.ExpectedMode != mode)
            throw new DomainException(
                $"Settings type mismatch: settings.$mode={settings.ExpectedMode} but source mode={mode}");

        // Auto-generate PublicKey for push sources if not provided
        if (publicKey is null && PublicKeyModes.Contains(mode))
            publicKey = GeneratePublicKey();

        return new()
        {
            Id                   = Guid.NewGuid(),
            TenantId             = tenantId,
            Code                 = code.Trim().ToUpperInvariant(),
            Label                = label.Trim(),
            Description          = description?.Trim(),
            ChannelType          = channelType,
            Mode                 = mode,
            Status               = LeadSourceStatus.Draft,
            PublicKey             = publicKey,
            Settings             = settings,
            PlatformConnectionId = platformConnectionId,
            DedupWindowDays      = dedupWindowDays,
            CostPerLead          = costPerLead,
            IsSystem             = isSystem,
            DisplayOrder         = displayOrder,
            CreatedAt            = DateTimeOffset.UtcNow,
        };
    }

    public static LeadSourceConfig CreateInternal(
        Guid tenantId, string code, string label, LeadChannelType channelType, int displayOrder)
        => Create(tenantId, code, label, channelType, displayOrder,
            integrationMode: IntegrationMode.Internal, isSystem: true);

    public void UpdateSettings(SourceSettings? settings)
    {
        if (settings is not null && settings.ExpectedMode != Mode)
            throw new DomainException(
                $"Settings type mismatch: settings.$mode={settings.ExpectedMode} but source mode={Mode}");
        Settings = settings;
    }

    /// <summary>
    /// Returns the list of field names that changed compared to the given values.
    /// </summary>
    public IReadOnlyList<string> Update(
        string label,
        string? description,
        int displayOrder,
        SourceSettings? settings = null,
        string? platformConnectionId = null,
        int? dedupWindowDays = null,
        Money? costPerLead = null)
    {
        // Active sources: PlatformConnectionId is immutable
        if (Status == LeadSourceStatus.Active && platformConnectionId != PlatformConnectionId)
            throw new DomainException("ACTIVE_SOURCE_IMMUTABLE_FIELDS");

        var changed = new List<string>();

        if (Label != label.Trim()) changed.Add(nameof(Label));
        if (Description != description?.Trim()) changed.Add(nameof(Description));
        if (DisplayOrder != displayOrder) changed.Add(nameof(DisplayOrder));
        if (PlatformConnectionId != platformConnectionId) changed.Add(nameof(PlatformConnectionId));
        if (dedupWindowDays.HasValue && DedupWindowDays != dedupWindowDays.Value) changed.Add(nameof(DedupWindowDays));
        if (costPerLead != CostPerLead) changed.Add(nameof(CostPerLead));
        if (settings != Settings) changed.Add(nameof(Settings));

        Label                = label.Trim();
        Description          = description?.Trim();
        DisplayOrder         = displayOrder;
        PlatformConnectionId = platformConnectionId;
        if (dedupWindowDays.HasValue) DedupWindowDays = dedupWindowDays.Value;
        CostPerLead = costPerLead;

        if (settings is not null)
            UpdateSettings(settings);
        else
            Settings = null;

        return changed;
    }

    // ── State machine ──────────────────────────────────────────────────────
    // Draft → Testing → Active ⇄ Paused
    // Active → Error → Active
    // Any non-system → Archived

    /// <summary>Transition to Testing. Only from Draft.</summary>
    public void StartTesting()
    {
        EnsureTransition(LeadSourceStatus.Testing, LeadSourceStatus.Draft);
        Status = LeadSourceStatus.Testing;
    }

    /// <summary>
    /// Transition to Active. Only from Testing, Paused, or Error.
    /// Returns activation prerequisite violations (empty = OK).
    /// </summary>
    public IReadOnlyList<string> Activate()
    {
        EnsureTransition(LeadSourceStatus.Active,
            LeadSourceStatus.Testing, LeadSourceStatus.Paused, LeadSourceStatus.Error);

        var missing = CheckActivationPrerequisites();
        if (missing.Count > 0) return missing;

        Status = LeadSourceStatus.Active;
        return [];
    }

    /// <summary>Transition to Paused. Only from Active.</summary>
    public void Pause()
    {
        EnsureTransition(LeadSourceStatus.Paused, LeadSourceStatus.Active);
        Status = LeadSourceStatus.Paused;
    }

    /// <summary>Transition to Error. Only from Active.</summary>
    public void MarkError()
    {
        EnsureTransition(LeadSourceStatus.Error, LeadSourceStatus.Active);
        Status = LeadSourceStatus.Error;
    }

    /// <summary>Transition to Archived. Any non-system status except Archived.</summary>
    public void Archive()
    {
        if (IsSystem)
            throw new DomainException("CANNOT_ARCHIVE_SYSTEM_SOURCE");
        if (Status == LeadSourceStatus.Archived)
            throw new DomainException("INVALID_TRANSITION");
        Status = LeadSourceStatus.Archived;
    }

    public void RotatePublicKey(string newKey) => PublicKey = newKey;

    // ── Activation prerequisites ────────────────────────────────────────

    private IReadOnlyList<string> CheckActivationPrerequisites()
    {
        var missing = new List<string>();

        // Push modes require a PublicKey (secret)
        if (PublicKeyModes.Contains(Mode) && string.IsNullOrEmpty(PublicKey))
            missing.Add("SecretMissing");

        // PlatformConnection requires a connection reference
        if (Mode == IntegrationMode.PlatformConnection && string.IsNullOrEmpty(PlatformConnectionId))
            missing.Add("ConnectionNotConnected");

        // Webhook/Pull with vault credentials — check settings
        switch (Settings)
        {
            case ServerWebhookSettings { SignatureAlgorithm: not null, SignatureCredentialVaultRef: null }:
                missing.Add("SecretMissing");
                break;
            case ScheduledPullSettings { EndpointUrl: "" or null }:
                missing.Add("NoEndpointConfigured");
                break;
            case ScheduledPullSettings { AuthCredentialVaultRef: null }:
                missing.Add("SecretMissing");
                break;
            case PlatformSettings { OAuthCredentialVaultRef: null }:
                missing.Add("SecretMissing");
                break;
        }

        // Modes that need field mapping must have at least phoneNumber + fullName mapped
        if (Mode is not IntegrationMode.Internal and not IntegrationMode.EmbeddedScript)
        {
            var fieldMapping = Settings switch
            {
                ServerWebhookSettings wh => wh.FieldMapping,
                ScheduledPullSettings pull => pull.FieldMapping,
                PlatformSettings plat => plat.FieldMapping,
                _ => null
            };

            if (fieldMapping is null || fieldMapping.Count == 0)
                missing.Add("NoFieldMappingConfigured");
        }

        return missing;
    }

    private void EnsureTransition(LeadSourceStatus target, params LeadSourceStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
            throw new DomainException("INVALID_TRANSITION");
    }
}

public enum LeadSourceStatus
{
    Draft,
    Testing,
    Active,
    Paused,
    Error,
    Archived
}
