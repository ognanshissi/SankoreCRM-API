namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateQualificationTemplateHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<CreateQualificationTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateQualificationTemplateCommand cmd, CancellationToken ct)
    {
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
                
            }
        }

        return Result.Ok(template);
    }

    private static IReadOnlyList<(Guid, string, QuestionRuleAction)>? MapRules(
        IReadOnlyList<RuleInput>? rules)
        => rules is { Count: > 0 }
            ? rules.Select(r => (r.TriggerQuestionId, r.TriggerValue, r.Action)).ToList()
            : null;
}
