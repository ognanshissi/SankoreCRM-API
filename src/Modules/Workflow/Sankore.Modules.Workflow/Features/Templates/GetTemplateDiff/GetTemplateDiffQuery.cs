using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.GetTemplateDiff;

/// <summary>
/// Compares two workflow template versions owned by the same tenant.
/// Steps are matched by <c>Order</c> (position in the circuit);
/// transitions are matched by event-code + source/target step orders.
/// </summary>
internal sealed record GetTemplateDiffQuery(
    Guid TemplateId,
    Guid CompareWithId
) : IRequest<Result<TemplateDiffDto>>;
