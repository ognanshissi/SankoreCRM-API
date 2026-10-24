namespace Sankore.Modules.Leads.Features.Opportunities.StepConditions;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The validation circuits a project went through, step by step and run by run — who approved,
/// who refused and why, who still has to. The project page shows it under « Validations »: M12
/// holds the decisions, this module knows which step of the project each run belongs to.
/// </summary>
internal sealed record GetProjectValidationsQuery(Guid OpportunityId)
    : IRequest<Result<IReadOnlyList<ProjectValidationDto>>>;

/// <param name="StepLabel">The project step that started the circuit; « Étape retirée » when the template no longer has it.</param>
/// <param name="Status">Pending, InProgress, Completed, Rejected, Cancelled, TimedOut — or Unknown when M12 could not be read.</param>
public sealed record ProjectValidationDto(
    Guid StepId,
    string StepLabel,
    string Circuit,
    string WorkflowName,
    Guid InstanceId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ProjectValidationDecisionDto> Decisions);

/// <param name="Status">Pending, AwaitingApproval, Approved, Rejected, Skipped, TimedOut…</param>
/// <param name="ApproverRole">The role expected to decide, by its display name.</param>
/// <param name="DecidedBy">Who approved or refused; null while pending.</param>
/// <param name="Comment">The approver's comment — the reason of a refusal.</param>
public sealed record ProjectValidationDecisionDto(
    int Order,
    string Name,
    string? ApproverRole,
    string Status,
    string? DecidedBy,
    string? Comment,
    DateTimeOffset? DueAt,
    DateTimeOffset? DecidedAt);

internal sealed class GetProjectValidationsHandler(
    LeadsDbContext db,
    ProjectTemplateResolver templates,
    IWorkflowModule workflow,
    IAdministrationModule administration,
    ITenantContext tenant,
    LeadVisibility? visibility = null)
    : IRequestHandler<GetProjectValidationsQuery, Result<IReadOnlyList<ProjectValidationDto>>>
{
    private const string RemovedStep = "Étape retirée";

    public async Task<Result<IReadOnlyList<ProjectValidationDto>>> Handle(
        GetProjectValidationsQuery query, CancellationToken ct)
    {
        // The same perimeter as the project itself: a project you cannot open has no validations to show you.
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        var project = await projects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == query.OpportunityId, ct);
        if (project is null)
            return Result.Fail<IReadOnlyList<ProjectValidationDto>>(OpportunityErrors.NotFound);
        if (project.WorkflowRuns.Count == 0)
            return Result.Ok<IReadOnlyList<ProjectValidationDto>>([]);

        IReadOnlyList<WorkflowInstanceDecisions> decisions;
        IReadOnlyDictionary<Guid, string> names;
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            decisions = await workflow.GetInstanceDecisionsAsync(
                tenant.CurrentTenantId, project.WorkflowRuns.Select(r => r.InstanceId).ToList(), ct);

            var deciders = decisions.SelectMany(d => d.Steps).Select(s => s.ActedByUserId).OfType<Guid>().Distinct().ToList();
            names = deciders.Count == 0
                ? new Dictionary<Guid, string>()
                : await administration.GetUserNamesAsync(tenant.CurrentTenantId, deciders, ct);
            suppressed.Complete();
        }

        var byInstance = decisions.ToDictionary(d => d.InstanceId);
        var template = await templates.ResolveAsync(project.Product, ct);

        return Result.Ok<IReadOnlyList<ProjectValidationDto>>(project.WorkflowRuns
            .OrderByDescending(r => r.StartedAt)
            .Select(run =>
            {
                var label = template.Steps.FirstOrDefault(s => s.Id == run.StepId)?.Label ?? RemovedStep;
                if (!byInstance.TryGetValue(run.InstanceId, out var d))
                    return new ProjectValidationDto(run.StepId, label, run.Circuit, run.Circuit, run.InstanceId,
                        "Unknown", run.StartedAt, null, []);

                return new ProjectValidationDto(
                    run.StepId, label, run.Circuit, d.WorkflowName, run.InstanceId, d.Status, d.StartedAt, d.CompletedAt,
                    d.Steps.Select(s => new ProjectValidationDecisionDto(
                        s.Order,
                        s.Name,
                        RoleName(s.ApproverRoleCode),
                        s.Status,
                        s.ActedByUserId is { } by ? names.GetValueOrDefault(by, "Utilisateur inconnu") : null,
                        s.Comment,
                        s.DueAt,
                        s.DecidedAt)).ToList());
            })
            .ToList());
    }

    private static string? RoleName(string? code)
        => code is null ? null : Roles.All.FirstOrDefault(r => r.Code == code)?.Name ?? code;
}
