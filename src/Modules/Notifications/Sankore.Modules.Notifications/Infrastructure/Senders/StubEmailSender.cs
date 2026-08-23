namespace Sankore.Modules.Notifications.Infrastructure.Senders;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// Development/test stub — logs the send intent instead of calling an email API.
/// Replace with SesEmailSender / PostmarkEmailSender / SendGridEmailSender in production.
/// </summary>
internal sealed class StubEmailSender(ILogger<StubEmailSender> logger) : IEmailSender
{
    public Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
    {
        logger.LogInformation(
            "(STUB) Email send | Provider={Provider} From={From} To={To} Subject={Subject} MessageId={Id}",
            provider.ProviderType,
            request.FromEmail,
            request.ToEmail,
            request.Subject,
            request.MessageId);

        return Task.CompletedTask;
    }
}
