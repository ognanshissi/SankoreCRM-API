namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>Where a step's validation circuit stands, as the gate and the screens read it.</summary>
public enum ProjectWorkflowState
{
    /// <summary>The step names a circuit but no run was started — a step skipped on the way.</summary>
    NotStarted,
    InProgress,
    Approved,
    /// <summary>Refused, cancelled or timed out: the project is blocked until lost or run again.</summary>
    Refused,
    /// <summary>M12 could not be read — fail-closed, read as « not approved ».</summary>
    Unknown,
}

/// <summary>
/// The validation circuits (M12) linked to project steps (product owner, 2026-10-24): started
/// AUTOMATICALLY when a project reaches a linked step, approved before it may leave it.
///
/// <para>
/// Every call crosses into M12's own DbContext, so it runs under a SUPPRESSED transaction scope —
/// a second connection enlisting in the command's ambient scope would be promoted to a distributed
/// transaction Npgsql refuses.
/// </para>
/// </summary>
internal sealed class ProjectWorkflows(
    IWorkflowModule? workflow,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock,
    Infrastructure.LeadsDbContext? db = null,
    Sankore.Modules.Customers.PublicApi.ICustomersModule? customers = null)
{
    /// <summary>M12's entity type for a project: the <see cref="Opportunity"/> aggregate.</summary>
    public const string EntityType = "Opportunity";

    /// <summary>The state of <paramref name="step"/>'s latest run on <paramref name="project"/>.</summary>
    public async Task<ProjectWorkflowState> StateAsync(Opportunity project, Guid stepId, CancellationToken ct)
    {
        var run = project.LatestWorkflowRun(stepId);
        if (run is null)
            return ProjectWorkflowState.NotStarted;
        if (workflow is null)
            return ProjectWorkflowState.Unknown;

        try
        {
            using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            var status = await workflow.GetInstanceStatusAsync(run.InstanceId, tenant.CurrentTenantId, ct);
            suppressed.Complete();

            if (status.IsFailure)
                return ProjectWorkflowState.Unknown;

            return status.Value!.Status switch
            {
                "Completed" => ProjectWorkflowState.Approved,
                "Rejected" or "Cancelled" or "TimedOut" => ProjectWorkflowState.Refused,
                _ => ProjectWorkflowState.InProgress,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return ProjectWorkflowState.Unknown;
        }
    }

    /// <summary>
    /// Starts <paramref name="step"/>'s circuit unless a run is already pending or approved — a
    /// project moved back and forth does not pile up instances, and one refused runs again on the
    /// way back in, as the « run again » button would. Records the run on the project; the caller
    /// saves.
    /// </summary>
    public async Task<Result> StartIfNeededAsync(Opportunity project, ProjectStepDto step, CancellationToken ct)
    {
        if (step.WorkflowCircuit is not { } circuit)
            return Result.Ok();

        var state = await StateAsync(project, step.Id, ct);
        if (state is ProjectWorkflowState.InProgress or ProjectWorkflowState.Approved)
            return Result.Ok();
        if (state is ProjectWorkflowState.Unknown && project.LatestWorkflowRun(step.Id) is not null)
            return Result.Fail(ProjectTemplateErrors.WorkflowUnavailable);

        return await StartAsync(project, step, circuit, ct);
    }

    /// <summary>Runs the circuit again after a refusal (or a run that never started).</summary>
    public async Task<Result> RestartAsync(Opportunity project, ProjectStepDto step, CancellationToken ct)
    {
        if (step.WorkflowCircuit is not { } circuit)
            return Result.Fail(ProjectTemplateErrors.WorkflowNotRestartable);

        var state = await StateAsync(project, step.Id, ct);
        if (state is not (ProjectWorkflowState.Refused or ProjectWorkflowState.NotStarted))
            return Result.Fail(ProjectTemplateErrors.WorkflowNotRestartable);

        return await StartAsync(project, step, circuit, ct);
    }

    private async Task<Result> StartAsync(Opportunity project, ProjectStepDto step, string circuit, CancellationToken ct)
    {
        var stepId = step.Id;
        if (workflow is null)
            return Result.Fail(ProjectTemplateErrors.WorkflowUnavailable);

        Result<Guid> started;
        try
        {
            using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            started = await workflow.StartWorkflowAsync(
                new WorkflowStartRequest(EntityType, project.Id, currentUser.Id, tenant.CurrentTenantId, Circuit: circuit,
                    Subject: await SubjectAsync(project, step, ct)), ct);
            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return Result.Fail(ProjectTemplateErrors.WorkflowUnavailable);
        }

        // No active template for the circuit (deactivated since the step was linked): refuse the
        // move rather than let the project past a validation nobody can give.
        if (started.IsFailure)
            return Result.Fail(ProjectTemplateErrors.WorkflowUnavailable);

        project.RecordWorkflowRun(stepId, circuit, started.Value, clock.GetUtcNow());
        return Result.Ok();
    }

    /// <summary>
    /// What the approver sees in their queue: the project, and one line of what the decision is
    /// about — who, which product, how much, at which step. Best-effort: a name that cannot be
    /// read is left out rather than block the submission.
    /// </summary>
    private async Task<WorkflowSubject> SubjectAsync(Opportunity project, ProjectStepDto step, CancellationToken ct)
    {
        string? person = null;
        try
        {
            if (project.LeadId is { } leadId && db is not null)
                person = await db.Leads.Where(l => l.Id == leadId).Select(l => l.FullName).FirstOrDefaultAsync(ct);
            else if (project.CustomerEntityId is { } clientId && customers is not null)
                person = (await customers.GetCustomerAsync(tenant.CurrentTenantId, clientId, ct))?.DisplayName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }

        var amount = project.EstimatedAmount is { Amount: > 0 } money
            ? string.Format(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), "{0:N0} {1}", money.Amount, money.Currency)
            : null;

        var detail = string.Join(" · ", new[]
        {
            person,
            string.IsNullOrWhiteSpace(project.Product) ? null : project.Product,
            amount,
            $"étape « {step.Label} »",
        }.Where(p => !string.IsNullOrWhiteSpace(p)));

        return new WorkflowSubject(project.Title, detail, $"/leads/projets/{project.Id}");
    }
}
