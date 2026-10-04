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
/// Meaningless together with <paramref name="AgentId"/>, and refused with it.
/// </param>
/// <param name="AgentId">
/// The agent to assign, naming them instead of letting the engine rank candidates. This is the
/// supervisor override: it answers "give this lead to that person", which no strategy can
/// express — every strategy ranks a pool, and the winner is whoever scores highest.
///
/// It is NOT a way past the configuration. The named agent still has to be one the Users module
/// returns as available for the lead's agency, and still has to clear the rule's exclusion list;
/// what is skipped is the ranking, and the load heuristics (see the handler).
///
/// Requires <paramref name="OverrideReason"/>: the assignment is recorded with
/// <c>WasManualOverride = true</c>, and an override whose reason is unknown is worse than no
/// record at all — <c>GetAssignmentHistory</c> already surfaces both fields.
/// </param>
/// <param name="OverrideReason">
/// Why this agent was chosen by hand. Required with <paramref name="AgentId"/>, refused without.
/// </param>
public sealed record DispatchLeadCommand(
    Guid LeadId,
    Guid TenantId,
    DispatchingStrategy? Strategy = null,
    Guid? AgentId = null,
    string? OverrideReason = null
) : IRequest<Result<DispatchLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
