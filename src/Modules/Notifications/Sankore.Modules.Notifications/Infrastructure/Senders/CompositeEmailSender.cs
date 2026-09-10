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
        var sender = provider.ProviderType switch
        {
            "Smtp"     => sp.GetRequiredKeyedService<IEmailSender>("smtp"),
            "Ses"      => sp.GetRequiredKeyedService<IEmailSender>("ses"),
            "Postmark" => sp.GetRequiredKeyedService<IEmailSender>("postmark"),
            "SendGrid" => sp.GetRequiredKeyedService<IEmailSender>("sendgrid"),
            _          => sp.GetRequiredKeyedService<IEmailSender>("stub"),
        };

        return sender.SendAsync(request, provider, ct);
    }
}