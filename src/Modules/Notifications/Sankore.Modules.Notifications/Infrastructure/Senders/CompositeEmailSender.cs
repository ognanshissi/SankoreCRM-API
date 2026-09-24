using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Notifications.Infrastructure.Providers;

namespace Sankore.Modules.Notifications.Infrastructure.Senders;

/// <summary>
/// Routes to the correct IEmailSender implementation based on the resolved provider type.
/// Registered as the primary IEmailSender. Individual senders are keyed services.
/// </summary>
internal sealed class CompositeEmailSender(IServiceProvider sp) : IEmailSender
{
    public Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
    {
        // "Default" is what TenantNotificationSettings.CreateDefault() writes — it means
        // "use the platform-wide provider", which is SMTP. Treat an empty value the same way.
        var key = provider.ProviderType?.Trim().ToLowerInvariant() switch
        {
            null or "" or "default" => "smtp",
            "smtp"                  => "smtp",
            "ses"                   => "ses",
            "postmark"              => "postmark",
            "sendgrid"              => "sendgrid",
            _                       => "stub",
        };

        var sender = sp.GetKeyedService<IEmailSender>(key)
            ?? throw new InvalidOperationException(
                $"No IEmailSender registered for provider '{provider.ProviderType}' (key '{key}').");

        return sender.SendAsync(request, provider, ct);
    }
}