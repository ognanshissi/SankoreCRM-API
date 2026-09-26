namespace Sankore.Modules.Leads.Features.QualificationTemplates.UnarchiveQualificationTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>Restores an archived template to Draft so it can be edited and published again.</summary>
internal sealed record UnarchiveQualificationTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => TemplateId.ToString();
}
