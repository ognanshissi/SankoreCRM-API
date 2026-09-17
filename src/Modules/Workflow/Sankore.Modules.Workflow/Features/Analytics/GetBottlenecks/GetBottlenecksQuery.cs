using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetBottlenecks;

/// <summary>
/// Ranks every step in a template by a composite bottleneck score:
///   score = avgDurationHours * 0.5 + rejectionRate * 0.3 + timeoutRate * 0.2
/// Steps with higher scores are the most likely bottlenecks in the approval circuit.
/// </summary>
internal sealed record GetBottlenecksQuery(Guid TemplateId)
    : IRequest<Result<IReadOnlyList<StepBottleneckDto>>>;
