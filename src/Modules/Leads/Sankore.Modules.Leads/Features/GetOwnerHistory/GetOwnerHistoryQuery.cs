namespace Sankore.Modules.Leads.Features.GetOwnerHistory;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetOwnerHistoryQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<OwnerAssignmentDto>>>;

public sealed record OwnerAssignmentDto(
    Guid Id,
    Guid? PreviousOwnerId,
    Guid NewOwnerId,
    string AssignmentMethod,
    string? Reason,
    Guid AssignedBy,
    DateTimeOffset AssignedAt);
