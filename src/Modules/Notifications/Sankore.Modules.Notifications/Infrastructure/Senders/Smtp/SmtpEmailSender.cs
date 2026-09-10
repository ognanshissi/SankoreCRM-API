using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Sankore.Modules.Notifications.Infrastructure.Providers;

namespace Sankore.Modules.Notifications.Infrastructure.Senders.Smtp;

/// <summary>
/// Sends email via SMTP using MailKit.
/// Credentials come from SmtpOptions (system-wide) bound from Notifications:Smtp in config.
/// FromEmail/FromName are overridden by the tenant's ResolvedEmailProvider when set.
/// </summary>
internal sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _opts = options.Value;

    public async Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
    {
        var message = BuildMessage(request);

        using var client = new SmtpClient();

        var secureSocketOptions = _opts.UseSsl ? SecureSocketOptions.SslOnConnect
            : _opts.UseStartTls ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(_opts.Host, _opts.Port, secureSocketOptions, ct);

        if (!string.IsNullOrEmpty(_opts.Username)
            && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await client.AuthenticateAsync(_opts.Username, _opts.Password, ct);

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);

        logger.LogInformation(
            "SMTP email sent | From={From} To={To} Subject={Subject} MessageId={Id}",
            request.FromEmail, request.ToEmail, request.Subject, request.MessageId);
    }

    private MimeMessage BuildMessage(SendEmailRequest request)
    {
        var message = new MimeMessage();

        message.From.Add(new MailboxAddress(request.FromName, request.FromEmail));
        message.To.Add(new MailboxAddress(request.ToName ?? request.ToEmail, request.ToEmail));

        if (!string.IsNullOrEmpty(request.ReplyToEmail))
            message.ReplyTo.Add(new MailboxAddress(request.ReplyToEmail, request.ReplyToEmail));

        message.Subject = request.Subject;

        var builder = new BodyBuilder
        {
            HtmlBody = request.HtmlBody,
            TextBody = request.TextBody
        };

        message.Body = builder.ToMessageBody();
        return message;
    }
}