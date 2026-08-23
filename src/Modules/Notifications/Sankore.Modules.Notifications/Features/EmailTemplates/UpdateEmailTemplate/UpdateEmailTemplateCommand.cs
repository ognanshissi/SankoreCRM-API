namespace Sankore.Modules.Notifications.Features.EmailTemplates.UpdateEmailTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates a new version of an existing template and deactivates all previous versions
/// for the same (TenantId, TemplateKey, Locale) combination.
/// </summary>
internal sealed record UpdateEmailTemplateCommand(
    Guid SourceTemplateId,
    string Subject,
    string HtmlBody,
    string? TextBody)
    : IRequest<Result<Guid>>, ICommand;
