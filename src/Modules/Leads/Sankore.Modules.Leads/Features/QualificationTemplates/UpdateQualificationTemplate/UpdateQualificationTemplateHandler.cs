namespace Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateQualificationTemplateHandler(LeadsDbContext db)
    : IRequestHandler<UpdateQualificationTemplateCommand, Result>
{
    public async Task<Result> Handle(
        UpdateQualificationTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.QualificationTemplates
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        var detailsResult = template.UpdateDetails(cmd.Name, cmd.Description, cmd.ProductName);
        if (detailsResult.IsFailure)
            return detailsResult;

        // Clear in-memory collections so domain order counters reset.
        template.ClearQuestionsAndSections();

        // Remove existing rows directly — avoids EF change-tracker orphan detection issues.
        await db.Set<QualificationQuestion>()
            .Where(q => q.TemplateId == cmd.TemplateId)
            .ExecuteDeleteAsync(ct);
        await db.Set<QualificationSection>()
            .Where(s => s.TemplateId == cmd.TemplateId)
            .ExecuteDeleteAsync(ct);

        var buildResult = CreateQualificationTemplateHandler.BuildQuestionsAndSections(
            template, cmd.Sections, cmd.Questions);
        if (buildResult.IsFailure)
            return buildResult;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
