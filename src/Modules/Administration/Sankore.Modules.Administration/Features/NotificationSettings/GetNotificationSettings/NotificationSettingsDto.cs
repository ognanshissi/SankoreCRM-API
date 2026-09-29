namespace Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;

public sealed record NotificationSettingsDto(
    string ProviderType,
    bool UseDefaultPlatformProvider,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    /// <summary>Whether a credential is stored in the vault. The secret itself is never returned.</summary>
    bool HasCredential,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpUseSsl,
    bool SmtpUseStartTls,
    int? MonthlyQuotaLimit,
    int CurrentMonthUsageCount,
    DateTimeOffset UpdatedAt);
