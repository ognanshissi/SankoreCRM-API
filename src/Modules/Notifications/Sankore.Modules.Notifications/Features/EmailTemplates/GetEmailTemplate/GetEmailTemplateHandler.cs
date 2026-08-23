namespace Sankore.Modules.Notifications.Features.EmailTemplates.GetEmailTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetEmailTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<GetEmailTemplateQuery, Result<EmailTemplateDetailDto>>
{
    public async Task<Result<EmailTemplateDetailDto>> Handle(
        GetEmailTemplateQuery request, CancellationToken ct)
    {
        var t = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.Id == request.Id
                        && (t.TenantId == currentUser.TenantId || t.TenantId == null))
            .FirstOrDefaultAsync(ct);

        if (t is null)
            return Result<EmailTemplateDetailDto>.Fail("EMAIL_TEMPLATE_NOT_FOUND");

        return Result<EmailTemplateDetailDto>.Ok(new EmailTemplateDetailDto(
            t.Id, t.TenantId, t.TemplateKey, t.Locale, t.Version,
            t.Subject, t.HtmlBody, t.TextBody, t.IsActive, t.CreatedAt));
    }
}
