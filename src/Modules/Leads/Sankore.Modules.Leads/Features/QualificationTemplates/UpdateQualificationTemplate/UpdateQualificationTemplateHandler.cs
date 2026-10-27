namespace Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateQualificationTemplateHandler(LeadsDbContext db, ITenantModules modules)
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

        if (template.Status == TemplateStatus.Archived)
            return Result.Fail("TEMPLATE_ALREADY_ARCHIVED");

        // Moving a template INTO a closed module's category is a creation in disguise; editing one
        // that already sits there is not — existing configuration stays usable.
        if (cmd.ProductCategory is { } category && category != template.ProductCategory
            && !await modules.AllowsCategoryAsync(category, ct))
            return Result.Fail(TenantModuleErrors.CategoryDisabled);

        var detailsResult = template.UpdateDetails(cmd.Name, cmd.Description, cmd.ProductCategory, cmd.ProductCode);
        if (detailsResult.IsFailure)
            return detailsResult;

        // After the details: a health questionnaire is checked against the category it now has.
        if (cmd.SubjectScope is not null || cmd.IsHealthQuestionnaire is not null || cmd.ExcludeFromScoring is not null)
        {
            var scope = template.SetSubjectScope(
                cmd.SubjectScope ?? template.SubjectScope,
                cmd.IsHealthQuestionnaire ?? template.IsHealthQuestionnaire,
                cmd.ExcludeFromScoring ?? template.ExcludeFromScoring);
            if (scope.IsFailure)
                return scope;
        }

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

        var built = buildResult.Value;
        db.Set<QualificationSection>().AddRange(built.Sections);
        db.Set<QualificationQuestion>().AddRange(built.Questions);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
