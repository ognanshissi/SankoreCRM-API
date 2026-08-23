namespace Sankore.Modules.Notifications.Infrastructure.Senders;

using Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// Low-level email transport abstraction.
/// Implementations: StubEmailSender (dev), SesEmailSender, PostmarkEmailSender, SendGridEmailSender (future).
/// </summary>
internal interface IEmailSender
{
    Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct);
}

/// <summary>Fully resolved send request ready for transport — no template data, only rendered content.</summary>
internal sealed record SendEmailRequest(
    Guid MessageId,
    Guid TenantId,
    string FromEmail,
    string FromName,
    string? ReplyToEmail,
    string ToEmail,
    string? ToName,
    string Subject,
    string HtmlBody,
    string? TextBody);
