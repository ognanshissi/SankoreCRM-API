namespace Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
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

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
