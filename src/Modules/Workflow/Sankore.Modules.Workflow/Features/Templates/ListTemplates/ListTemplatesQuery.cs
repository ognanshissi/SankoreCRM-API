using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ListTemplates;

public sealed record ListTemplatesQuery(bool? ActiveOnly = null)
    : IRequest<Result<List<WorkflowTemplateDto>>>;
