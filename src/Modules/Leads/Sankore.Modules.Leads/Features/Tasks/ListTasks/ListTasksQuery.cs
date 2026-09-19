namespace Sankore.Modules.Leads.Features.Tasks.ListTasks;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Shared.Kernel;

internal sealed record ListTasksQuery(
    Guid? LeadId = null,
    Guid? AssignedAgentId = null,
    CrmTaskStatus? Status = null,
    CrmTaskType? Type = null
) : IRequest<Result<IReadOnlyList<CrmTaskDto>>>;
