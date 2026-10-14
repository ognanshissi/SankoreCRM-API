namespace Sankore.Modules.Notifications.Infrastructure.Sms.Senders;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Routes to the right <see cref="ISmsSender"/> for the resolved provider. Registered as the
/// primary sender; the implementations are keyed services — the same arrangement
/// <c>CompositeEmailSender</c> uses.
/// </summary>
internal sealed class CompositeSmsSender(IServiceProvider sp) : ISmsSender
{
    public Task<SmsSendResult> SendAsync(
        SendSmsRequest request, ResolvedSmsProvider provider, CancellationToken ct)
    {
        // Unlike the email composite there is NO fallback to a platform provider and no "stub"
        // default for an unknown value: a message the processor cannot place must fail loudly and
        // dead-letter, because the alternative on this channel is silence that costs money
        // somewhere else. An unconfigured channel never gets here at all — the processor refuses
        // it on the quota check, which answers CHANNEL_NOT_CONFIGURED.
        var key = provider.ProviderType?.Trim().ToLowerInvariant() switch
        {
            "brevo" => "brevo",
            "twilio" => "twilio",
            _ => null,
        };

        if (key is null)
        {
            throw new InvalidOperationException(
                $"No SMS sender for provider '{provider.ProviderType}'. "
                + "The tenant's SMS channel is not configured.");
        }

        var sender = sp.GetKeyedService<ISmsSender>(key)
            ?? throw new InvalidOperationException(
                $"No ISmsSender registered for provider '{provider.ProviderType}' (key '{key}').");

        return sender.SendAsync(request, provider, ct);
    }
}
