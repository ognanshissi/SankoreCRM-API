using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetExecutionMonitor;

/// <summary>
/// Returns a real-time execution health snapshot:
///   - Active instance counts per template
///   - AwaitingApproval step queue depth per template
///   - Stuck instances: InProgress for more than <see cref="StuckThresholdHours"/> hours
///     with no SLA-triggered timeout
/// </summary>
internal sealed record GetExecutionMonitorQuery(int StuckThresholdHours = 48)
    : IRequest<Result<ExecutionMonitorDto>>;
