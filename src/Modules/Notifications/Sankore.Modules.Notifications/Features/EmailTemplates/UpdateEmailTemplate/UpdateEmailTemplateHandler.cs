namespace Sankore.Modules.Notifications.Features.EmailTemplates.UpdateEmailTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpdateEmailTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<UpdateEmailTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UpdateEmailTemplateCommand request, CancellationToken ct)
    {
        var source = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                t => t.Id == request.SourceTemplateId
                     && (t.TenantId == currentUser.TenantId || t.TenantId == null),
                ct);

        if (source is null)
            return Result<Guid>.Fail("EMAIL_TEMPLATE_NOT_FOUND");

        // Compute next version
        var nextVersion = await db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == source.TenantId
                        && t.TemplateKey == source.TemplateKey
                        && t.Locale == source.Locale)
            .Select(t => (int?)t.Version)
            .MaxAsync(ct) ?? 0;

        // Deactivate all existing versions for this scope
        var existing = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == source.TenantId
                        && t.TemplateKey == source.TemplateKey
                        && t.Locale == source.Locale
                        && t.IsActive)
            .ToListAsync(ct);

        foreach (var t in existing)
            t.Deactivate();

        // Create new active version
        var newTemplate = EmailTemplate.Create(
            source.TenantId,
            source.TemplateKey,
            source.Locale,
            nextVersion + 1,
            request.Subject,
            request.HtmlBody,
            request.TextBody);

        db.EmailTemplates.Add(newTemplate);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(newTemplate.Id);
    }
}
