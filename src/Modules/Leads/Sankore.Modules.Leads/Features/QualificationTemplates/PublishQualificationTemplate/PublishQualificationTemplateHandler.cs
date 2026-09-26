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

        // Check first, write nothing yet: archiving the incumbent below is flushed
        // immediately, and a template that turns out to be unpublishable must not
        // take the live one down with it.
        var canPublish = template.CanPublish();
        if (canPublish.IsFailure)
            return canPublish;

        // Auto-archive the template currently live for this product category, so at most
        // one is Published per (TenantId, ProductCategory).
        //
        // This is flushed BEFORE the new one is published. A single SaveChanges would let
        // EF order the two UPDATEs freely, and publishing first trips the filtered unique
        // index on (tenant_id, product_category) WHERE status = 'Published'. Both writes
        // still commit or roll back together under the ambient TransactionScope that
        // TransactionBehavior opens for every ICommand.
        if (template.ProductCategory is not null)
        {
            var superseded = await db.QualificationTemplates
                .AsTracking()
                .Where(t => t.Id != cmd.TemplateId
                            && t.TenantId == template.TenantId
                            && t.ProductCategory == template.ProductCategory
                            && t.Status == TemplateStatus.Published)
                .ToListAsync(ct);

            if (superseded.Count > 0)
            {
                foreach (var prev in superseded)
                    prev.Archive();

                await db.SaveChangesAsync(ct);
            }
        }

        var result = template.Publish(clock.GetUtcNow());
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
