namespace Sankore.Modules.Notifications.Features.EmailTemplates.ListEmailTemplates;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListEmailTemplatesQuery(
    string? TemplateKey = null,
    string? Locale = null,
    bool? IsActive = null)
    : IRequest<Result<List<EmailTemplateDto>>>;

internal sealed record EmailTemplateDto(
    Guid Id,
    Guid? TenantId,
    string TemplateKey,
    string Locale,
    int Version,
    string Subject,
    bool IsActive,
    DateTimeOffset CreatedAt);
