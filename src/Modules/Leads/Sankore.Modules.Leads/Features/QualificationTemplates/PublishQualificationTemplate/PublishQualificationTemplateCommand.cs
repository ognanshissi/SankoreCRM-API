namespace Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record PublishQualificationTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => TemplateId.ToString();
}
