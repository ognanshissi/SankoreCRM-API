namespace Sankore.Modules.Leads.Features.Tasks.GetTask;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record GetTaskQuery(Guid TaskId) : IRequest<Result<CrmTaskDto>>;

public sealed record CrmTaskDto(
    Guid Id,
    Guid TenantId,
    CrmTaskType Type,
    CrmTaskPriority Priority,
    string Title,
    string? Description,
    Guid? LeadId,
    Guid? AssignedAgentId,
    CrmTaskStatus Status,
    DateTimeOffset DueAt,
    DateTimeOffset? SlaDeadline,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? TriggerEventType,
    Guid? TriggerEventId);
