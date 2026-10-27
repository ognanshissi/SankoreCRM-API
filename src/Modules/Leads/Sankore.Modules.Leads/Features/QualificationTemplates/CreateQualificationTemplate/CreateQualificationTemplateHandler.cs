namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateQualificationTemplateHandler(LeadsDbContext db, TimeProvider clock, ITenantModules modules)
    : IRequestHandler<CreateQualificationTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateQualificationTemplateCommand cmd, CancellationToken ct)
    {
        if (cmd.ProductCategory is { } category && !await modules.AllowsCategoryAsync(category, ct))
            return Result.Fail<Guid>(TenantModuleErrors.CategoryDisabled);

        var template = QualificationTemplate.Create(
            tenantId:    cmd.TenantId,
            name:        cmd.Name,
            now:         clock.GetUtcNow(),
            description:  cmd.Description,
            productCategory:  cmd.ProductCategory,
            productCode:  cmd.ProductCode);

        var buildResult = BuildQuestionsAndSections(template, cmd.Sections, cmd.Questions);
        if (buildResult.IsFailure)
            return Result.Fail<Guid>(buildResult.Error!);

        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        return Result.Ok(template.Id);
    }

    internal static Result<QualificationTemplate> BuildQuestionsAndSections(
        QualificationTemplate template,
        IReadOnlyList<SectionInput>? sections,
        IReadOnlyList<QuestionInput>? questions)
    {
        // Every question is created under a new id, the update included (it deletes and rebuilds),
        // while a rule names its trigger by the id the editor read. Without this map a rule saved
        // twice points at a question that no longer exists: a « Show » rule then hides its
        // question for good, on every questionnaire published from the template.
        var idMap = new Dictionary<Guid, Guid>();
        void Remember(QuestionInput input)
        {
            if (input.Id is { } sentId) idMap[sentId] = template.Questions[^1].Id;
        }

        if (sections is { Count: > 0 })
        {
            foreach (var sectionInput in sections)
            {
                var sectionResult = template.AddSection(sectionInput.Title, sectionInput.Description);
                if (sectionResult.IsFailure)
                    return Result.Fail<QualificationTemplate>(sectionResult.Error!);

                foreach (var q in sectionInput.Questions ?? [])
                {
                    var rules = MapRules(q.Rules);
                    var qResult = template.AddQuestion(
                        q.Label, q.Type, q.Weight, q.IsRequired,
                        sectionId: sectionResult.Value!.Id,
                        options: q.Options, helpText: q.HelpText,
                        placeholderText: q.PlaceholderText,
                        minValue: q.MinValue, maxValue: q.MaxValue,
                        rules: rules);
                    if (qResult.IsFailure)
                        return Result.Fail<QualificationTemplate>(qResult.Error!);
                    Remember(q);
                }
            }
        }
        else if (questions is { Count: > 0 })
        {
            foreach (var q in questions)
            {
                var rules = MapRules(q.Rules);
                var qResult = template.AddQuestion(
                    q.Label, q.Type, q.Weight, q.IsRequired,
                    options: q.Options, helpText: q.HelpText,
                    placeholderText: q.PlaceholderText,
                    minValue: q.MinValue, maxValue: q.MaxValue,
                    rules: rules);
                if (qResult.IsFailure)
                    return Result.Fail<QualificationTemplate>(qResult.Error!);
                Remember(q);
            }
        }

        foreach (var question in template.Questions)
            question.RemapRuleTriggers(idMap);

        return Result.Ok(template);
    }

    private static IReadOnlyList<(Guid, string, QuestionRuleAction)>? MapRules(
        IReadOnlyList<RuleInput>? rules)
        => rules is { Count: > 0 }
            ? rules.Select(r => (r.TriggerQuestionId, r.TriggerValue, r.Action)).ToList()
            : null;
}
