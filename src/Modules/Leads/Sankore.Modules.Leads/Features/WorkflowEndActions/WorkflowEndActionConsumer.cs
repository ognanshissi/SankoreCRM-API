namespace Sankore.Modules.Leads.Features.WorkflowEndActions;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs the action of a workflow « Fin » step on a lead or a project (product owner, 2026-11-02),
/// through the ordinary commands so the record's own rules apply — a project the circuit wins is
/// still refused if a point of its step is unchecked. Whatever happens is said back
/// (<see cref="WorkflowEndActionOutcome"/>) and written into the circuit's history: the circuit has
/// ended either way, an action that could not run is for a person to finish.
///
/// <para>
/// A redelivered message finds the record already where the action leaves it and says so, rather
/// than failing on « already closed ».
/// </para>
/// </summary>
public sealed class WorkflowEndActionConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowEndActionConsumer> logger) : IConsumer<WorkflowEndActionRequested>
{
    public async Task Consume(ConsumeContext<WorkflowEndActionRequested> context)
    {
        var request = context.Message;
        if (WorkflowEndActions.Find(request.Action) is not { } action
            || !string.Equals(action.EntityType, request.EntityType, StringComparison.OrdinalIgnoreCase)
            || action.EntityType is not ("Lead" or "Opportunity"))
            return;

        using var bgCtx = BackgroundJobContext.SetScope(request.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var ct = context.CancellationToken;

        var (done, message) = action.Code switch
        {
            WorkflowEndActions.ProjectWin or WorkflowEndActions.ProjectLose => await CloseProjectAsync(scope.ServiceProvider, request, ct),
            _ => await ActOnLeadAsync(scope.ServiceProvider, request, ct),
        };

        logger.LogInformation("Workflow end action {Action} on {EntityType} {EntityId}: {Message}",
            request.Action, request.EntityType, request.EntityId, message);
        await context.Publish(new WorkflowEndActionOutcome(request.TenantId, request.InstanceId, request.InstanceStepId, done, message), ct);
    }

    private static async Task<(bool, string)> CloseProjectAsync(IServiceProvider sp, WorkflowEndActionRequested request, CancellationToken ct)
    {
        var db = sp.GetRequiredService<LeadsDbContext>();
        var win = request.Action == WorkflowEndActions.ProjectWin;
        var target = win ? OpportunityStage.ClosedWon : OpportunityStage.ClosedLost;

        var stage = await db.Opportunities.IgnoreQueryFilters()
            .Where(o => o.TenantId == request.TenantId && o.Id == request.EntityId)
            .Select(o => (OpportunityStage?)o.Stage)
            .FirstOrDefaultAsync(ct);
        if (stage is null) return (false, "Action non faite : le projet n'existe plus.");
        if (stage == target) return (true, win ? "Le projet était déjà gagné." : "Le projet était déjà perdu.");
        if (stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return (false, "Action non faite : le projet était déjà clos.");

        var reason = win ? null : $"Refusé par le circuit « {request.TemplateName ?? "de validation"} »";
        var result = await sp.GetRequiredService<ISender>().Send(new CloseOpportunityCommand(request.EntityId, target, reason), ct);

        return result.IsSuccess
            ? (true, win ? "Projet gagné par le circuit." : "Projet perdu par le circuit.")
            : (false, $"Action non faite : {Explain(result.Error)}");
    }

    private static async Task<(bool, string)> ActOnLeadAsync(IServiceProvider sp, WorkflowEndActionRequested request, CancellationToken ct)
    {
        var db = sp.GetRequiredService<LeadsDbContext>();
        var lead = await db.Leads.IgnoreQueryFilters().AsTracking()
            .FirstOrDefaultAsync(l => l.TenantId == request.TenantId && l.Id == request.EntityId, ct);
        if (lead is null) return (false, "Action non faite : le lead n'existe plus.");

        switch (request.Action)
        {
            case WorkflowEndActions.LeadQualify:
                if (lead.Status == LeadStatus.Qualified) return (true, "Le lead était déjà qualifié.");
                // The same move as a project opened for the lead: a decision, not a score.
                if (!lead.QualifyByProject())
                    return (false, "Action non faite : le lead ne peut plus être qualifié depuis son statut actuel.");
                await db.SaveChangesAsync(ct);
                return (true, "Lead qualifié par le circuit.");

            default:
            {
                var archive = request.Action == WorkflowEndActions.LeadArchive;
                var target = archive ? LeadStatus.Archived : LeadStatus.Lost;
                if (lead.Status == target) return (true, archive ? "Le lead était déjà archivé." : "Le lead était déjà perdu.");

                var detail = $"Décidé par le circuit « {request.TemplateName ?? "de validation"} »";
                var result = await sp.GetRequiredService<ISender>().Send(
                    new CloseLeadCommand(lead.Id, archive ? LeadCloseReason.Archived : LeadCloseReason.Lost, detail), ct);
                return result.IsSuccess
                    ? (true, archive ? "Lead archivé par le circuit." : "Lead marqué perdu par le circuit.")
                    : (false, $"Action non faite : {Explain(result.Error)}");
            }
        }
    }

    /// <summary>The refusal in words; the code itself when it has none here.</summary>
    private static string Explain(string? error) => error switch
    {
        null or "" => "refus sans motif.",
        var e when e.StartsWith("Étape bloquante", StringComparison.Ordinal) => e,
        var e when e.Contains(' ') => e,
        var code => $"refusé ({code}).",
    };
}
