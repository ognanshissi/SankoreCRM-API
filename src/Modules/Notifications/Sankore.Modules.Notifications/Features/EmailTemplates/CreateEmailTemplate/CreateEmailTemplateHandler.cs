namespace Sankore.Modules.Notifications.Features.EmailTemplates.CreateEmailTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CreateEmailTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<CreateEmailTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateEmailTemplateCommand request, CancellationToken ct)
    {
        var tenantId = request.IsGlobal ? (Guid?)null : currentUser.TenantId;

        // Compute next version: max existing + 1 (or 1 if none)
        var maxVersion = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId
                        && t.TemplateKey == request.TemplateKey
                        && t.Locale == request.Locale)
            .Select(t => (int?)t.Version)
            .MaxAsync(ct) ?? 0;

        var template = EmailTemplate.Create(
            tenantId,
            request.TemplateKey,
            request.Locale,
            maxVersion + 1,
            request.Subject,
            request.HtmlBody,
            request.TextBody);

        db.EmailTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(template.Id);
    }
}
