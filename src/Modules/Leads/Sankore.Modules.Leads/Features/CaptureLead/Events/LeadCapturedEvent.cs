namespace Sankore.Modules.Leads.Features.CaptureLead.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published through the outbox whenever a lead is created, whatever the channel — web form,
/// webhook, mobile agent, file import, counter.
///
/// It exists so that auto-qualification and auto-dispatch happen OUT of the capture transaction:
/// a lead must be saved even when no agent is available, and a 400-row import must not perform
/// 400 synchronous dispatches — each of which calls the Administration module for the agent pool
/// and Redis for capacity — inside the import job's loop.
/// </summary>
/// <param name="HasExplicitOwner">
/// The capture already named an owner (an import column, an agent's own collection). Auto-dispatch
/// must not overrule a human decision, and the consumer cannot tell after the fact whether
/// OwnerId came from the capture or from a later assignment.
/// </param>
public sealed record LeadCapturedEvent(
    Guid LeadId,
    Guid TenantId,
    string Source,
    bool HasExplicitOwner,
    bool DuplicateSuspected) : IntegrationEventBase;
