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
    /// Active provider: "Default" | "Ses" | "Postmark" | "SendGrid".
    /// "Default" means the platform-wide SES account is used.
    /// </summary>
    public string ProviderType { get; private set; } = "Default";

    public bool UseDefaultPlatformProvider { get; private set; } = true;

    public string? FromEmail { get; private set; }
    public string? FromName { get; private set; }
    public string? ReplyToEmail { get; private set; }

    /// <summary>Custom sending domain required by some providers (e.g. Postmark).</summary>
    public string? SendingDomain { get; private set; }

    /// <summary>
    /// Vault path reference only — e.g. "secret/tenants/{tenantId}/notifications/ses".
    /// The actual secret is never persisted in this table.
    /// </summary>
    public string? CredentialVaultPath { get; private set; }

    /// <summary>Null = unlimited.</summary>
    public int? MonthlyQuotaLimit { get; private set; }

    /// <summary>Running count for the current calendar month.</summary>
    public int CurrentMonthUsageCount { get; private set; }

    /// <summary>First day of the month currently tracked by CurrentMonthUsageCount.</summary>
    public DateTimeOffset? CurrentMonthStartedAt { get; private set; }

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

    public static readonly string[] AllowedProviders = ["Default", "Ses", "Postmark", "SendGrid"];

    public void UpdateProvider(
        string providerType,
        string? fromEmail,
        string? fromName,
        string? replyToEmail,
        string? sendingDomain,
        string? credentialVaultPath,
        Guid updatedBy)
    {
        ProviderType = providerType;
        UseDefaultPlatformProvider = providerType == "Default";
        FromEmail = fromEmail;
        FromName = fromName;
        ReplyToEmail = replyToEmail;
        SendingDomain = sendingDomain;
        CredentialVaultPath = credentialVaultPath;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void ResetToDefault(Guid updatedBy)
    {
        ProviderType = "Default";
        UseDefaultPlatformProvider = true;
        // CredentialVaultPath intentionally kept for audit trail
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void SetMonthlyQuota(int? quotaLimit, Guid updatedBy)
    {
        MonthlyQuotaLimit = quotaLimit;
        UpdatedAt = DateTimeOffset.UtcNow;
        UpdatedBy = updatedBy;
    }

    public void IncrementUsage()
    {
        var now = DateTimeOffset.UtcNow;
        if (CurrentMonthStartedAt is null
            || CurrentMonthStartedAt.Value.Month != now.Month
            || CurrentMonthStartedAt.Value.Year != now.Year)
        {
            CurrentMonthUsageCount = 0;
            CurrentMonthStartedAt = now;
        }

        CurrentMonthUsageCount++;
    }
}
