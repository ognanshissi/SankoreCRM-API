using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

/// <param name="Credential">
/// The SMTP password, or the provider's API key for Brevo and the others. Written to the vault,
/// never to a column. Leave it null to keep the credential already stored — which is what lets
/// an administrator change the From address without re-typing a password they may not have.
/// </param>
public sealed record UpdateNotificationSettingsCommand(
    string ProviderType,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    [property: SensitiveData] string? Credential,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpUseSsl,
    bool SmtpUseStartTls) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantNotificationSettings";
    public string? ResourceId => null;
}
