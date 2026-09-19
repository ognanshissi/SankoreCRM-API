namespace Sankore.Modules.Leads.Features.QualificationTemplates.ArchiveQualificationTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ArchiveQualificationTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => TemplateId.ToString();
}
