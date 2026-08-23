using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;

public record GetNotificationSettingsQuery : IRequest<Result<NotificationSettingsDto>>;
