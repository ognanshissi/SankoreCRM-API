using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetSlaDashboard;

/// <summary>
/// Returns SLA breach metrics across all templates for a date window:
///   - breach rate per template
///   - daily breach trend within the window
///   - currently overdue active steps (DueAt &lt; now, Status = AwaitingApproval)
/// </summary>
internal sealed record GetSlaDashboardQuery(
    DateTimeOffset From,
    DateTimeOffset To)
    : IRequest<Result<SlaDashboardDto>>;
