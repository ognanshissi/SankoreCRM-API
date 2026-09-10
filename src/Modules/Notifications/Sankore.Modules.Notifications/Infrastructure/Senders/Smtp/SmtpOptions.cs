namespace Sankore.Modules.Notifications.Infrastructure.Senders.Smtp;

internal sealed class SmtpOptions
{
    public const string SectionName = "Notifications:Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>true → implicit TLS (port 465).</summary>
    public bool UseSsl { get; set; } = false;

    /// <summary>true → STARTTLS (port 587). false → plain (port 1025, MailDev, etc.).</summary>
    public bool UseStartTls { get; set; } = false;

    public string FromEmail { get; set; } = "noreply@sankore.io";
    public string FromName { get; set; } = "Sankore";
}