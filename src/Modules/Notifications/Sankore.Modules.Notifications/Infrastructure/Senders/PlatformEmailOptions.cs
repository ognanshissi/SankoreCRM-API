namespace Sankore.Modules.Notifications.Infrastructure.Senders;

using Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// The platform's own email account: what a tenant with no provider of its own sends through.
/// Chosen once, at installation (<c>Notifications:Platform</c>), and never per tenant — a tenant
/// that wants another provider configures it on <c>PUT /notification-settings</c>.
///
/// <c>Smtp</c> keeps the historical behaviour (the relay of <c>Notifications:Smtp</c>);
/// <c>Brevo</c> sends through Brevo's API with <see cref="BrevoApiKey"/>. The key sits in the
/// deployment's environment and not in the vault: the vault is per tenant, and this account
/// belongs to no tenant.
/// </summary>
internal sealed class PlatformEmailOptions
{
    public const string SectionName = "Notifications:Platform";

    public const string Smtp = "Smtp";
    public const string Brevo = "Brevo";

    public string Provider { get; set; } = Smtp;

    public string? BrevoApiKey { get; set; }

    /// <summary>
    /// The sender of every platform email. Unset falls back to <c>Notifications:Smtp:FromEmail</c>,
    /// which the documentation has always told operators to set — and which nothing read, the
    /// address being hard-coded to <c>noreply@sankore.io</c>. Required with Brevo, which refuses a
    /// sender the account has not verified.
    /// </summary>
    public string? FromEmail { get; set; }

    public string? FromName { get; set; }

    /// <summary>The keyed <see cref="IEmailSender"/> a "Default" provider is routed to.</summary>
    public string SenderKey => IsBrevo ? "brevo" : "smtp";

    public bool IsBrevo => string.Equals(Provider?.Trim(), Brevo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Who a message is from. The platform's identity whenever the platform account sends — a
    /// tenant's address on the platform's Brevo account would be refused as unverified, and on
    /// its SMTP relay would be a spoof — otherwise the tenant's, completed by the platform's.
    /// </summary>
    public (string Email, string Name) SenderFor(ResolvedEmailProvider provider) => provider.IsDefault
        ? (FromEmail!, FromName!)
        : (provider.FromEmail ?? FromEmail!, provider.FromName ?? FromName!);
}
