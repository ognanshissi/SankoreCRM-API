using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.GetTemplate;

public sealed record GetTemplateQuery(Guid TemplateId)
    : IRequest<Result<WorkflowTemplateDto>>;
