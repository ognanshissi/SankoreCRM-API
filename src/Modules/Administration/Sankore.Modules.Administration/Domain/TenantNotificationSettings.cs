namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Per-tenant email provider configuration (Epic 1 — F12.9).
/// Credentials are never stored here — only a reference path pointing to the vault.
/// One row per tenant; created on first access with platform-default values.
/// </summary>
public sealed class TenantNotificationSettings
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Active provider: "Smtp" | "Ses" | "Postmark" | "SendGrid".
    /// "Default" means the platform-wide SES account is used.
    /// </summary>
    public string ProviderType { get; private set; } = "Smtp";

    public bool UseDefaultPlatformProvider { get; private set; } = true;

    public string? FromEmail { get; private set; }
    public string? FromName { get; private set; }
    public string? ReplyToEmail { get; private set; }

    /// <summary>Custom sending domain required by some providers (e.g. Postmark).</summary>
    public string? SendingDomain { get; private set; }

    /// <summary>
    /// Whether a credential for <see cref="ProviderType"/> is held in the vault. A flag, not the
    /// secret and not a path: the location is derived from the tenant and the provider by
    /// <c>NotificationSecrets.CredentialKey</c>, so nothing here has to be kept in step with it.
    /// </summary>
    public bool HasCredential { get; private set; }

    // ── SMTP relay, used when ProviderType is "Smtp" ────────────────────────
    public string? SmtpHost { get; private set; }
    public int? SmtpPort { get; private set; }
    public string? SmtpUsername { get; private set; }

    /// <summary>Implicit TLS (port 465).</summary>
    public bool SmtpUseSsl { get; private set; }

    /// <summary>STARTTLS (port 587).</summary>
    public bool SmtpUseStartTls { get; private set; }

    /// <summary>Null = unlimited.</summary>
    public int? MonthlyQuotaLimit { get; private set; }

    /// <summary>Running count for the current calendar month.</summary>
    public int CurrentMonthUsageCount { get; private set; }

    /// <summary>First day of the month currently tracked by CurrentMonthUsageCount.</summary>
    public DateTimeOffset? CurrentMonthStartedAt { get; private set; }

    /// <summary>
    /// PostgreSQL xmin. Two instances can process the outbox at the same moment, and the quota
    /// is the one field where a lost update means sending past a limit an institution set.
    /// </summary>
    public uint Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    private TenantNotificationSettings() { }

    public static TenantNotificationSettings CreateDefault(Guid tenantId, Guid createdBy) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ProviderType = "Default",
        UseDefaultPlatformProvider = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = createdBy
    };

    /// <summary>
    /// "Default" means the platform's own account. Everything else is the tenant's own provider.
    /// </summary>
    public static readonly string[] AllowedProviders =
        ["Default", "Smtp", "Brevo", "Ses", "Postmark", "SendGrid"];

    /// <summary>Providers whose credential is an API key rather than an SMTP password.</summary>
    public static bool UsesApiKey(string providerType)
        => providerType is "Brevo" or "Ses" or "Postmark" or "SendGrid";

    public void UpdateProvider(
        string providerType,
        string? fromEmail,
        string? fromName,
        string? replyToEmail,
        string? sendingDomain,
        SmtpRelaySettings? smtp,
        Guid updatedBy)
    {
        ProviderType = providerType;

        // Only "Default" means the platform account. The previous rule said Smtp did, which had
        // it exactly backwards: choosing your own SMTP relay marked you as using the platform's.
        UseDefaultPlatformProvider = providerType == "Default";

        FromEmail = fromEmail;
        FromName = fromName;
        ReplyToEmail = replyToEmail;
        SendingDomain = sendingDomain;

        if (smtp is not null)
        {
            SmtpHost = smtp.Host;
            SmtpPort = smtp.Port;
            SmtpUsername = smtp.Username;
            SmtpUseSsl = smtp.UseSsl;
            SmtpUseStartTls = smtp.UseStartTls;
        }

        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Records that a credential was written to (or removed from) the vault. Separate from
    /// <see cref="UpdateProvider"/> so the flag can only ever be set by the code that actually
    /// touched the vault.
    /// </summary>
    public void SetCredentialPresence(bool hasCredential, Guid updatedBy)
    {
        HasCredential = hasCredential;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void ResetToDefault(Guid updatedBy)
    {
        ProviderType = "Default";
        UseDefaultPlatformProvider = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void SetMonthlyQuota(int? quotaLimit, Guid updatedBy)
    {
        MonthlyQuotaLimit = quotaLimit;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Counts one message against this month's allowance, or refuses it.
    ///
    /// Check and increment are one operation ON PURPOSE: splitting them lets two concurrent
    /// senders both read "999 of 1000" and both send. The caller still needs optimistic
    /// concurrency on <see cref="Version"/> to make that guarantee hold across processes.
    /// </summary>
    public bool TryConsumeMonthlyQuota()
    {
        RollOverMonthIfNeeded();

        // Null limit means unlimited — still counted, so usage stays visible.
        if (MonthlyQuotaLimit is { } limit && CurrentMonthUsageCount >= limit)
            return false;

        CurrentMonthUsageCount++;
        return true;
    }

    /// <summary>
    /// Resets the counter when the calendar month turned. Done lazily on first use of the month
    /// rather than by a scheduled job: a job that fails to run would silently block a tenant for
    /// a whole month.
    /// </summary>
    private void RollOverMonthIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;

        if (CurrentMonthStartedAt is { } started
            && started.Month == now.Month
            && started.Year == now.Year)
        {
            return;
        }

        CurrentMonthUsageCount = 0;
        CurrentMonthStartedAt = now;
    }
}

/// <summary>Connection settings of a tenant's own SMTP relay. The password lives in the vault.</summary>
public sealed record SmtpRelaySettings(
    string Host,
    int Port,
    string? Username,
    bool UseSsl,
    bool UseStartTls);
