namespace Sankore.Modules.Leads.Features.QualificationTemplates.DuplicateQualificationTemplate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Copies an existing template — Draft, Published or Archived — into a new editable Draft.
/// </summary>
/// <param name="TemplateId">Template to copy from.</param>
/// <param name="Name">Name for the copy. When omitted, the source name with a " (copy)" suffix.</param>
internal sealed record DuplicateQualificationTemplateCommand(Guid TemplateId, string? Name)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";

    // The audited resource is the template being copied; the copy's id is the result.
    public string? ResourceId => TemplateId.ToString();
}
