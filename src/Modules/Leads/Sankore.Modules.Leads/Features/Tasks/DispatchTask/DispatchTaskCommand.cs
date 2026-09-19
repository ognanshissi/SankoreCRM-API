namespace Sankore.Modules.Leads.Features.Tasks.DispatchTask;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Dispatches a CRM task to the best available agent using the compatibility
/// engine from US-M13-072. The task must be linked to a lead (LeadId != null)
/// so the scorer can evaluate language, product, geography and agency factors.
/// Implements ICommand → wrapped by Transaction + Audit pipeline behaviors.
/// </summary>
public sealed record DispatchTaskCommand(
    Guid TaskId,
    Guid TenantId,
    DispatchingStrategy Strategy = DispatchingStrategy.CompatibilityScoring
) : IRequest<Result<DispatchTaskResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}

public sealed record DispatchTaskResult(
    Guid AgentId,
    string AgentName,
    double CompatibilityScore,
    string CompatibilityFactorsJson);
