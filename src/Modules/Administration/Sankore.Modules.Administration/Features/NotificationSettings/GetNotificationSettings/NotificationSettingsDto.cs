namespace Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;

public sealed record NotificationSettingsDto(
    string ProviderType,
    bool UseDefaultPlatformProvider,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    /// <summary>Never the real secret — only a masked reference path.</summary>
    string? CredentialVaultPathRef,
    int? MonthlyQuotaLimit,
    int CurrentMonthUsageCount,
    DateTimeOffset UpdatedAt);
