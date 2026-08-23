namespace Sankore.Modules.Notifications.Features.EmailTemplates.CreateEmailTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateEmailTemplateCommand(
    string TemplateKey,
    string Locale,
    string Subject,
    string HtmlBody,
    string? TextBody,
    /// <summary>When true TenantId is set to null (platform-wide template). Requires platform admin.</summary>
    bool IsGlobal = false)
    : IRequest<Result<Guid>>, ICommand;
