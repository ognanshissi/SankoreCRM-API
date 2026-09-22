namespace Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class PublishQualificationTemplateHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<PublishQualificationTemplateCommand, Result>
{
    public async Task<Result> Handle(
        PublishQualificationTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .AsTracking()
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        var result = template.Publish(clock.GetUtcNow());
        if (result.IsFailure)
            return result;

        // Auto-archive the previous Published template for this product category (if any).
        // Ensures at most one Published template per (TenantId, ProductCategory).
        if (template.ProductCategory is not null)
        {
            var superseded = await db.QualificationTemplates
                .AsTracking()
                .Where(t => t.Id != cmd.TemplateId
                            && t.TenantId == template.TenantId
                            && t.ProductCategory == template.ProductCategory
                            && t.Status == TemplateStatus.Published)
                .ToListAsync(ct);

            foreach (var prev in superseded)
                prev.Archive();
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
