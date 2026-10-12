using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateStep;

internal sealed record UpdateStepCommand(
    Guid TemplateId,
    Guid StepId,
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours,
    StepKind? Kind = null,
    string? ConfigJson = null,
    bool? IsBlocking = null
) : IRequest<Result>, ICommand;
