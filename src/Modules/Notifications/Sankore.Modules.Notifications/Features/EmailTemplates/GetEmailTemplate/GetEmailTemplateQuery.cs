namespace Sankore.Modules.Notifications.Features.EmailTemplates.GetEmailTemplate;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetEmailTemplateQuery(Guid Id) : IRequest<Result<EmailTemplateDetailDto>>;

internal sealed record EmailTemplateDetailDto(
    Guid Id,
    Guid? TenantId,
    string TemplateKey,
    string Locale,
    int Version,
    string Subject,
    string HtmlBody,
    string? TextBody,
    bool IsActive,
    DateTimeOffset CreatedAt);
