namespace Sankore.Modules.Leads.Features.DispatchLead.PreviewDispatch;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// "Who would this lead go to, and why?" — the dispatching engine's answer, without dispatching.
///
/// Deliberately NOT an <c>ICommand</c>: it must skip TransactionBehavior and AuditBehavior, and
/// it publishes nothing. The reassignment screen used to get this information by POSTing
/// <c>dispatchLead</c> with a strategy and reading the result, which assigned the lead, started
/// its SLA and notified an agent as a side effect of opening a drawer. A ranked list is a read;
/// this is the read.
/// </summary>
public sealed record PreviewDispatchQuery(Guid LeadId)
    : IRequest<Result<DispatchPreviewResult>>;

/// <param name="Strategy">The strategy the applicable rule carries — what a real dispatch would use.</param>
/// <param name="RuleId">The rule that would apply, or null when the built-in defaults would.</param>
/// <param name="WouldAssignToAgentId">
/// Who a dispatch right now would actually pick: the highest-scoring candidate that clears every
/// filter. Null when none would — the preview then still lists the candidates and says what
/// blocks each one, which is the whole point of previewing.
/// </param>
public sealed record DispatchPreviewResult(
    Guid LeadId,
    DispatchingStrategy Strategy,
    Guid? RuleId,
    string? RuleName,
    int MaxTasksPerAgent,
    int AntiMonopolyThreshold,
    Guid? WouldAssignToAgentId,
    IReadOnlyList<DispatchPreviewCandidate> Candidates);

/// <param name="CompatibilityScore">
/// The score the configured strategy actually computes — not an estimate. This is what the
/// front-end could only approximate before, and the reason the screen reached for a POST.
/// </param>
/// <param name="IsEligible">
/// True when nothing blocks this agent. Blocked candidates are returned rather than filtered out
/// so the screen can show them greyed with a reason; hiding them is what makes "why is this
/// agent not in the list?" unanswerable.
/// </param>
public sealed record DispatchPreviewCandidate(
    Guid AgentId,
    string FullName,
    Guid AgencyId,
    double CompatibilityScore,
    string CompatibilityFactorsJson,
    int OpenTaskCount,
    int HotLeadsCount,
    bool IsExcludedByRule,
    bool IsAtTaskCapacity,
    bool IsBlockedByAntiMonopoly,
    bool IsEligible);
