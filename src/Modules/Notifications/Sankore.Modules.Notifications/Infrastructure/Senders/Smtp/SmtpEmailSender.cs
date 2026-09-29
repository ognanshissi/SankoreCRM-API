using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Sankore.Modules.Notifications.Infrastructure.Providers;

namespace Sankore.Modules.Notifications.Infrastructure.Senders.Smtp;

/// <summary>
/// Sends through SMTP with MailKit, using the TENANT's own relay when it has configured one and
/// falling back to the platform account otherwise.
///
/// The password is fetched from the vault per send rather than held in options: an operator can
/// rotate it without a restart, and it never sits in the Redis cache next to the rest of the
/// configuration.
/// </summary>
internal sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    INotificationCredentials credentials,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _platform = options.Value;

    public async Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
    {
        var connection = await ResolveConnectionAsync(request.TenantId, provider, ct);

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            // Failing loudly beats a message the outbox marks as sent while nothing left the box.
            throw new InvalidOperationException(
                "No SMTP host configured: set Notifications:Smtp for the platform, or an SMTP relay on the tenant.");
        }

        var message = BuildMessage(request);

        using var client = new SmtpClient
        {
            // Without this a dead SMTP host blocks the outbox processor indefinitely,
            // leaving the message claimed as Sending with no attempt recorded.
            Timeout = (int)TimeSpan.FromSeconds(_platform.TimeoutSeconds).TotalMilliseconds,
        };

        var socketOptions = connection.UseSsl ? SecureSocketOptions.SslOnConnect
            : connection.UseStartTls ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(connection.Host, connection.Port, socketOptions, ct);

        if (!string.IsNullOrEmpty(connection.Username)
            && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
        {
            // An empty password is still a valid credential for some relays (and MailDev needs
            // none at all); a null one is not something MailKit accepts.
            await client.AuthenticateAsync(connection.Username, connection.Password ?? string.Empty, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);

        logger.LogInformation(
            "SMTP email sent | Relay={Relay} Host={Host} From={From} To={To} MessageId={Id}",
            connection.IsTenantRelay ? "tenant" : "platform",
            connection.Host, request.FromEmail, request.ToEmail, request.MessageId);
    }

    private async Task<SmtpConnection> ResolveConnectionAsync(
        Guid tenantId, ResolvedEmailProvider provider, CancellationToken ct)
    {
        // A tenant relay only counts when it actually names a host; a half-filled form must not
        // silently redirect that tenant's mail to the platform account under a wrong From.
        var useTenantRelay = provider.ProviderType == "Smtp"
                             && !string.IsNullOrWhiteSpace(provider.SmtpHost);

        if (!useTenantRelay)
        {
            return new SmtpConnection(
                _platform.Host, _platform.Port, _platform.Username, _platform.Password,
                _platform.UseSsl, _platform.UseStartTls, IsTenantRelay: false);
        }

        var password = await credentials.GetAsync(tenantId, provider, ct);

        return new SmtpConnection(
            provider.SmtpHost!,
            provider.SmtpPort ?? (provider.SmtpUseSsl ? 465 : 587),
            provider.SmtpUsername,
            password,
            provider.SmtpUseSsl,
            provider.SmtpUseStartTls,
            IsTenantRelay: true);
    }

    private static MimeMessage BuildMessage(SendEmailRequest request)
    {
        var message = new MimeMessage();

        message.From.Add(new MailboxAddress(request.FromName, request.FromEmail));
        message.To.Add(new MailboxAddress(request.ToName ?? request.ToEmail, request.ToEmail));

        if (!string.IsNullOrEmpty(request.ReplyToEmail))
            message.ReplyTo.Add(new MailboxAddress(request.ReplyToEmail, request.ReplyToEmail));

        message.Subject = request.Subject;
        message.Body = new BodyBuilder
        {
            HtmlBody = request.HtmlBody,
            TextBody = request.TextBody,
        }.ToMessageBody();

        return message;
    }

    private readonly record struct SmtpConnection(
        string Host, int Port, string? Username, string? Password,
        bool UseSsl, bool UseStartTls, bool IsTenantRelay);
}
