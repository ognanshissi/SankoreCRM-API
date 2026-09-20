namespace Sankore.Modules.Notifications.Features.EmailTemplates.ForkEmailTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ForkEmailTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<ForkEmailTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ForkEmailTemplateCommand request, CancellationToken ct)
    {
        var source = await db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.SourceTemplateId, ct);

        if (source is null)
            return Result<Guid>.Fail("EMAIL_TEMPLATE_NOT_FOUND");

        if (!source.IsSystem)
            return Result<Guid>.Fail("NOT_A_SYSTEM_TEMPLATE");

        var tenantId = currentUser.TenantId;

        // Check if a tenant override already exists for this (TemplateKey, Locale)
        var overrideExists = await db.EmailTemplates
            .IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == tenantId
                           && t.TemplateKey == source.TemplateKey
                           && t.Locale == source.Locale, ct);

        if (overrideExists)
            return Result<Guid>.Fail("TENANT_OVERRIDE_EXISTS");

        var fork = source.Fork(tenantId);

        db.EmailTemplates.Add(fork);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(fork.Id);
    }
}
