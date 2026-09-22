namespace Sankore.Modules.Leads.Features.QualificationTemplates.UpdateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateQualificationTemplateCommand(
    Guid TemplateId,
    string Name,
    string? Description,
    string? ProductCategory,
    string? ProductCode,
    IReadOnlyList<QuestionInput>? Questions,
    IReadOnlyList<SectionInput>? Sections
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => TemplateId.ToString();
}
