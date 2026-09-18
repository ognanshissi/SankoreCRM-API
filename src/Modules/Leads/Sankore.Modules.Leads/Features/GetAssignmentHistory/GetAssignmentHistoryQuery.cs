namespace Sankore.Modules.Leads.Features.GetAssignmentHistory;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record GetAssignmentHistoryQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<AssignmentDto>>>;

public sealed record AssignmentDto(
    Guid Id,
    Guid AgentId,
    DispatchingStrategy Strategy,
    double CompatibilityScore,
    bool WasManualOverride,
    string? OverrideReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset SlaDeadline,
    DateTimeOffset? FirstContactAt,
    bool SlaBreached);
