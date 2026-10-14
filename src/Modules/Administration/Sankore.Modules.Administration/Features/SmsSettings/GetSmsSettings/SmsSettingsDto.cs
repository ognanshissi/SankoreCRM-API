namespace Sankore.Modules.Administration.Features.SmsSettings.GetSmsSettings;

/// <summary>
/// The SMS channel as the settings screen shows it.
///
/// <para>
/// <b>No credential, ever — only whether one is stored.</b> The same rule as
/// <c>NotificationSettingsDto</c>: an administrator who needs to change a sender id must not have
/// to re-type an API key, so the screen shows "a key is stored" and the write slice keeps it when
/// the field is left blank.
/// </para>
/// </summary>
/// <param name="AvailableProviders">
/// The tokens the write endpoint accepts, served with the configuration so the screen's options
/// cannot drift from the server's own list.
/// </param>
/// <param name="CanSend">
/// Whether a message would actually go out. The one field the screen needs to decide between
/// "configured" and "half-configured" — a tenant with a provider and no sender identity looks
/// configured and sends nothing.
/// </param>
/// <param name="SenderIdHint">
/// What this provider expects in <c>SenderId</c>, in the administrator's language. Server-side
/// because the rule belongs to the provider, and a screen that hard-coded it would say "11
/// characters maximum" after somebody switched to Twilio.
/// </param>
public sealed record SmsSettingsDto(
    string ProviderType,
    string? SenderId,
    string? AccountSid,
    string? DefaultCallingCode,
    bool HasCredential,
    bool CanSend,
    int? MonthlyQuotaLimit,
    int CurrentMonthUsageCount,
    bool RequiresAccountSid,
    string? SenderIdHint,
    IReadOnlyList<string> AvailableProviders);
