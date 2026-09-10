namespace Sankore.Modules.Notifications.Domain;

public enum EmailProviderType
{
    Default,
    Smtp,
    Ses,
    Postmark,
    SendGrid
}
