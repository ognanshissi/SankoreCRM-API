namespace Sankore.Modules.Notifications.Features.EmailTemplates.ListEmailTemplates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ListEmailTemplatesHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<ListEmailTemplatesQuery, Result<List<EmailTemplateDto>>>
{
    public async Task<Result<List<EmailTemplateDto>>> Handle(
        ListEmailTemplatesQuery request, CancellationToken ct)
    {
        var query = db.EmailTemplates
            .IgnoreQueryFilters()
            // Return tenant-specific + platform templates visible to this tenant
            .Where(t => t.TenantId == currentUser.TenantId || t.TenantId == null)
            .AsQueryable();

        if (request.TemplateKey is not null)
            query = query.Where(t => t.TemplateKey == request.TemplateKey);

        if (request.Locale is not null)
            query = query.Where(t => t.Locale == request.Locale);

        if (request.IsActive is not null)
            query = query.Where(t => t.IsActive == request.IsActive.Value);

        var list = await query
            .OrderBy(t => t.TemplateKey)
            .ThenBy(t => t.Locale)
            .ThenBy(t => t.Version)
            .Select(t => new EmailTemplateDto(
                t.Id, t.TenantId, t.TemplateKey, t.Locale, t.Version, t.Subject, t.IsActive, t.CreatedAt))
            .ToListAsync(ct);

        return Result<List<EmailTemplateDto>>.Ok(list);
    }
}
