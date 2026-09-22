namespace Sankore.Modules.Leads.Features.QualificationTemplates;

using Sankore.Modules.Leads.Domain;

internal static class QualificationTemplateMappings
{
    public static QualificationTemplateDto ToDto(QualificationTemplate t) => new(
        t.Id,
        t.Name,
        t.Description,
        t.ProductCategory,
        t.ProductCode,
        t.Status.ToString(),
        t.Version,
        t.PublishedAt,
        t.CreatedAt,
        t.Sections
            .OrderBy(s => s.Order)
            .Select(s => new QualificationSectionDto(s.Id, s.Title, s.Description, s.Order))
            .ToList(),
        t.Questions
            .OrderBy(q => q.Order)
            .Select(q =>
            {
                var rules = q.GetRules()
                    .Select(r => new QuestionRuleDto(
                        r.TriggerQuestionId,
                        r.TriggerValue,
                        r.Action.ToString()))
                    .ToList();
                return new QualificationQuestionDto(
                    q.Id, q.SectionId, q.Label, q.HelpText, q.PlaceholderText,
                    q.Type.ToString(), q.GetOptions(), q.Weight, q.IsRequired, q.Order,
                    q.MinValue, q.MaxValue, rules);
            })
            .ToList());
}
