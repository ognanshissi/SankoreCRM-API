namespace Sankore.Modules.Notifications.Features.EmailTemplates.ActivateEmailTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ActivateEmailTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<ActivateEmailTemplateCommand, Result>
{
    public async Task<Result> Handle(ActivateEmailTemplateCommand request, CancellationToken ct)
    {
        var target = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                t => t.Id == request.TemplateId
                     && (t.TenantId == currentUser.TenantId || t.TenantId == null),
                ct);

        if (target is null)
            return Result.Fail("EMAIL_TEMPLATE_NOT_FOUND");

        // Deactivate all other versions for this (TenantId, Key, Locale)
        var siblings = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == target.TenantId
                        && t.TemplateKey == target.TemplateKey
                        && t.Locale == target.Locale
                        && t.Id != target.Id
                        && t.IsActive)
            .ToListAsync(ct);

        foreach (var t in siblings)
            t.Deactivate();

        target.Activate();
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
