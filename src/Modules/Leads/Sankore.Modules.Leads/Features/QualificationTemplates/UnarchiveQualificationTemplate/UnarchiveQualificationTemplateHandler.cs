namespace Sankore.Modules.Leads.Features.QualificationTemplates.UnarchiveQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UnarchiveQualificationTemplateHandler(LeadsDbContext db)
    : IRequestHandler<UnarchiveQualificationTemplateCommand, Result>
{
    public async Task<Result> Handle(
        UnarchiveQualificationTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        var result = template.Unarchive();
        if (result.IsFailure)
            return result;

        // Restores to Draft, so it cannot collide with the template currently live for
        // this product category — that contention is resolved on publish.
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
