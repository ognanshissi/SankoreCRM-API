using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

public sealed record UpdateNotificationSettingsCommand(
    string ProviderType,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    string? CredentialVaultPath) : IRequest<Result>, ICommand;
