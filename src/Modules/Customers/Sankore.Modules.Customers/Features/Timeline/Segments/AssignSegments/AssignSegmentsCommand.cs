namespace Sankore.Modules.Customers.Features.Timeline.Segments.AssignSegments;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Re-evaluates the segment of every live client of one tenant (US-M01-BE-27).
///
/// Implements <see cref="ICommand"/> so the nightly run is wrapped in a transaction AND
/// written to the audit trail — under the SYSTEM account, since
/// <c>AssignSegmentsJob</c> establishes that identity before sending it. An automatic
/// reclassification that moves a client out of PREMIUM has to be as traceable as a manual one.
///
/// The tenant is an explicit parameter, not read from the ambient context: the caller is a
/// Hangfire job iterating over tenants.
/// </summary>
public sealed record AssignSegmentsCommand(Guid TenantId)
    : IRequest<Result<AssignSegmentsResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientSegment";
    public string? ResourceId => TenantId.ToString("D");
}

/// <param name="ClientsEvaluated">Live clients examined (neither archived nor merged).</param>
/// <param name="SegmentsChanged">Clients whose segment actually changed — one event published each.</param>
/// <param name="RulesApplied">Evaluable rules in the tenant's rule set.</param>
/// <param name="RulesSkipped">Rules ignored because they need outstanding data from M03/M04.</param>
public sealed record AssignSegmentsResult(
    int ClientsEvaluated,
    int SegmentsChanged,
    int RulesApplied,
    int RulesSkipped);
