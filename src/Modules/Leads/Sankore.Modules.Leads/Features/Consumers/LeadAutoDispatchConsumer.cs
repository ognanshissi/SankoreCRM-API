namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Scores, qualifies and dispatches a freshly captured lead, when the tenant asked for it
/// (<c>Leads:AutoDispatchOnCapture</c>).
///
/// Out of the capture transaction on purpose: the lead must persist even when no agent is free,
/// and a 400-row import must not run 400 synchronous dispatches — each calling the Administration
/// module for the agent pool and Redis for capacity — inside its loop.
///
/// Idempotency comes from the lead itself rather than from an inbox table: a replayed message
/// finds <c>CurrentAssignmentId</c> already set and stops. That is exact, because the assignment
/// and the lead commit together.
/// </summary>
public sealed class LeadAutoDispatchConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<LeadModuleSettings> settings,
    ILogger<LeadAutoDispatchConsumer> logger)
    : IConsumer<LeadCapturedEvent>
{
    public async Task Consume(ConsumeContext<LeadCapturedEvent> context)
    {
        var evt = context.Message;

        if (!settings.Value.AutoDispatchOnCapture) return;

        // SYSTEM placeholder: nothing to do for a tenant that does not exist.
        if (evt.TenantId == Guid.Empty) return;

        if (evt.HasExplicitOwner)
        {
            logger.LogDebug(
                "Lead {LeadId} was captured with an owner — auto-dispatch skipped", evt.LeadId);
            return;
        }

        if (evt.DuplicateSuspected)
        {
            // Warn mode created the lead but flagged it. Routing a suspected duplicate to an
            // agent wastes the agent's time and pollutes the other lead's history.
            logger.LogInformation(
                "Lead {LeadId} is a suspected duplicate — auto-dispatch skipped", evt.LeadId);
            return;
        }

        // The ambient SYSTEM scope must be established BEFORE any scoped service is resolved:
        // ITenantContext and ICurrentUser are built from it, and a consumer has no HTTP context
        // to fall back on. Hence the explicit scope rather than constructor injection.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();
        var sender = sp.GetRequiredService<ISender>();
        var ct = context.CancellationToken;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == evt.LeadId, ct);

        if (lead is null)
        {
            logger.LogWarning("Lead {LeadId} not found for auto-dispatch", evt.LeadId);
            return;
        }

        if (lead.CurrentAssignmentId is not null) return;   // already dispatched — replayed message
        if (!lead.IsDispatchable) return;                   // closed between capture and delivery

        // ── 1. Score and qualify ──────────────────────────────────────────
        // Through the normal command so the score history, the intent level and the audit trail
        // are the ones every other qualification produces. Path 3 of QualifyLeadHandler
        // (no template, no explicit score) is the attribute-based calculator.
        var qualification = await sender.Send(
            new QualifyLeadCommand(
                LeadId: lead.Id,
                QualifiedBy: Guid.Empty,
                TriggerEvent: "AUTO_ON_CAPTURE"),
            ct);

        if (qualification.IsFailure)
        {
            logger.LogWarning(
                "Auto-qualification of lead {LeadId} failed: {Error}", lead.Id, qualification.Error);
            return;
        }

        logger.LogInformation(
            "Lead {LeadId} auto-qualified: score={Score}, status={Status} (threshold {Threshold})",
            lead.Id, qualification.Value.Score, qualification.Value.Status,
            settings.Value.QualificationThreshold);

        // ── 2. Dispatch ───────────────────────────────────────────────────
        // Strategy left null: DispatchingRuleResolver picks the applicable rule and the rule
        // carries the strategy.
        var dispatch = await sender.Send(
            new DispatchLeadCommand(lead.Id, evt.TenantId, Strategy: null), ct);

        if (dispatch.IsFailure)
        {
            // Expected outcomes, not incidents: no agent free, everyone at task capacity, the
            // lead closed meanwhile. LeadDispatchingFailedEvent already notifies the branch
            // manager from inside the handler.
            logger.LogInformation(
                "Lead {LeadId} captured but not dispatched: {Reason}", lead.Id, dispatch.Error);
            return;
        }

        logger.LogInformation(
            "Lead {LeadId} auto-dispatched to agent {AgentId}",
            lead.Id, dispatch.Value.AgentId);
    }
}
