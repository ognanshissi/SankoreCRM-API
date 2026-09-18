using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.GetCompletedTasks;

/// <summary>
/// Returns paginated workflow steps that the given user has completed (acted on or was assigned to
/// when the step reached a terminal state). Terminal = Approved, AutoApproved, Rejected, TimedOut, Skipped.
/// </summary>
internal sealed record GetCompletedTasksQuery(
    Guid UserId,
    int Page     = 1,
    int PageSize = 20)
    : IRequest<Result<PagedResult<CompletedTaskDto>>>;
