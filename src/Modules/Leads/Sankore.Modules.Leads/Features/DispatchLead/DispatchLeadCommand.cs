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
public sealed record DispatchLeadCommand(
    Guid LeadId,
    Guid TenantId,
    DispatchingStrategy Strategy = DispatchingStrategy.CompatibilityScoring
) : IRequest<Result<DispatchLeadResult>>, ICommand;
