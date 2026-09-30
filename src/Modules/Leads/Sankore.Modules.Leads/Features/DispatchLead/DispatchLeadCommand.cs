namespace Sankore.Modules.Leads.Features.DispatchLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Command: dispatch a Sales Qualified lead to the best available agent.
/// Implements ICommand so the shared Audit/Transaction pipeline behaviors
/// automatically wrap it — no extra wiring needed in this slice.
/// </summary>
/// <param name="Strategy">
/// Strategy the caller insists on. <c>null</c> — the automatic path — lets
/// <c>DispatchingRuleResolver</c> pick the applicable rule and take ITS strategy, which is the
/// only way to start from the lead rather than from a strategy someone typed.
/// </param>
public sealed record DispatchLeadCommand(
    Guid LeadId,
    Guid TenantId,
    DispatchingStrategy? Strategy = null
) : IRequest<Result<DispatchLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
