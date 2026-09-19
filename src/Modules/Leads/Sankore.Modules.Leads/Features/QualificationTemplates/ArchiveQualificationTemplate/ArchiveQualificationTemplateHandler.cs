namespace Sankore.Modules.Leads.Features.QualificationTemplates.ArchiveQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ArchiveQualificationTemplateHandler(LeadsDbContext db)
    : IRequestHandler<ArchiveQualificationTemplateCommand, Result>
{
    public async Task<Result> Handle(
        ArchiveQualificationTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        var result = template.Archive();
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
