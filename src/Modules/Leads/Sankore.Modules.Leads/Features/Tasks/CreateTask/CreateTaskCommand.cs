namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <param name="LeadId">The former way to attach a lead, kept for the lead's Tâches tab; same as
/// <c>EntityType = Lead</c>.</param>
/// <param name="EntityType">Lead, Client, KycFile or Project — or null for a task about nothing in particular.</param>
/// <param name="ParticipantIds">Colleagues associated besides the holder.</param>
/// <param name="Criteria">What « done » means, one line each, in order.</param>
internal sealed record CreateTaskCommand(
    CrmTaskType Type,
    CrmTaskPriority Priority,
    string Title,
    DateTimeOffset DueAt,
    Guid? LeadId = null,
    Guid? AssignedAgentId = null,
    DateTimeOffset? SlaDeadline = null,
    string? Description = null,
    TaskEntityType? EntityType = null,
    Guid? EntityId = null,
    IReadOnlyList<Guid>? ParticipantIds = null,
    IReadOnlyList<string>? Criteria = null,
    IReadOnlyList<string>? Tags = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => (EntityId ?? LeadId)?.ToString();
}
