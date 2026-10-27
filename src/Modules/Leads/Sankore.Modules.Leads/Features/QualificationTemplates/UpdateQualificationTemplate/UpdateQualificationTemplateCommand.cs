namespace Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateQualificationTemplateCommand(
    Guid TemplateId,
    string Name,
    string? Description,
    ProductCategory? ProductCategory,
    string? ProductCode,
    IReadOnlyList<QuestionInput>? Questions,
    IReadOnlyList<SectionInput>? Sections,
    /// <summary>Null keeps the current scope.</summary>
    QualificationSubjectScope? SubjectScope = null,
    /// <summary>Null keeps the current flag.</summary>
    bool? IsHealthQuestionnaire = null,
    /// <summary>Null keeps the current flag; forced on for a health questionnaire.</summary>
    bool? ExcludeFromScoring = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => TemplateId.ToString();
}
