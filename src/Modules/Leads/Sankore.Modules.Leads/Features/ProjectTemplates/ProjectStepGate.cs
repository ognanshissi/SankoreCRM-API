namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// What a project needs before it reaches a marked step of its template (lot 3).
///
/// <list type="bullet">
/// <item><b>« Questionnaires complets »</b> — every questionnaire the product asks, of the file and
/// of each person, answered. Checked first: the agent fills them before converting. Winning a
/// project whose template marks no such step still needs the PER-PERSON ones, which is the
/// guarantee the conversion used to give (no contract without the health declarations); the file's
/// questionnaire is then not required. An unreadable catalogue refuses.</item>
/// <item><b>« Ouvre le dossier client »</b> — a client record. For a product that requires KYC,
/// this is where it opens: the agent opens the client file from the lead (which opens the KYC
/// file), and a lead whose client has no KYC file yet gets one requested here.</item>
/// <item><b>« Approbation »</b>, and winning the project — winning IS the approval when the
/// template has none — a client, and for a product that requires KYC, a file APPROVED at the tier
/// the product asks for. For a product that requires none (a Standard line whose policy says
/// None), only the client: it is created active when the agent opens it at this step, which is the
/// product owner's rule — a no-KYC customer exists once the project is approved, not before. The
/// lead converts there (<c>ProjectLeadConversion</c>).</item>
/// </list>
///
/// <para>
/// <b>Refuses, never creates a client by itself.</b> Opening a client file can stop on things only
/// the agent can settle — an incomplete file, a probable duplicate, an identity document already on
/// someone else — so the gate answers <c>PROJECT_CLIENT_FILE_REQUIRED</c> and the screen opens the
/// wizard the agent already knows. A template with no marked step gates only the WIN: the built-in
/// default marks none.
/// </para>
///
/// <para>
/// <b>Fail-closed on the KYC question</b>: an unresolved product, an unreadable policy or an
/// unreachable KYC module all read as « KYC required, not approved ».
/// </para>
/// </summary>
internal sealed class ProjectStepGate(
    LeadsDbContext db,
    IAdministrationModule administration,
    IKycModule kyc,
    ITenantContext tenant,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    QuestionnaireCompletenessChecker? questionnaires = null,
    ProjectWorkflows? workflows = null)
{
    /// <param name="targetIndex">
    /// The index of the step reached in <paramref name="template"/>; <c>Steps.Count</c> for a project
    /// being won, which reaches every step.
    /// </param>
    /// <param name="currentIndex">Where the project stands now, -1 when nowhere (closed).</param>
    public async Task<Result> CheckAsync(
        Opportunity project,
        ResolvedProjectTemplate template,
        int currentIndex,
        int targetIndex,
        CancellationToken ct)
    {
        var steps = template.Steps.ToList();

        // The step conditions (product owner, 2026-10-24) first: they are the project's own data,
        // cheap to read, and what the agent can settle on the spot.
        var conditions = await ConditionsMetAsync(project, steps, currentIndex, targetIndex, ct);
        if (conditions.IsFailure)
            return conditions;

        var opens = steps.FindIndex(s => s.OpensClientFile);
        var approval = steps.FindIndex(s => s.IsApproval);
        var asks = steps.FindIndex(s => s.RequiresQuestionnaires);

        // Winning reaches past the last step: the per-person declarations are owed then, marked or
        // not — a contract is not opened on a file whose declarations were never collected.
        var reachesMarker = asks >= 0 && targetIndex >= asks;
        var wins = targetIndex >= steps.Count;
        if ((reachesMarker || wins) && questionnaires is not null)
        {
            var answered = await QuestionnairesAnsweredAsync(project, includeDossier: reachesMarker, ct);
            if (answered.IsFailure)
                return answered;
        }

        var reachesFile = opens >= 0 && targetIndex >= opens;
        // Winning IS approving when the template has no approval step (product owner, 2026-10-24):
        // the lead converts then, so it needs its client — and the KYC the product asks.
        var reachesApproval = (approval >= 0 && targetIndex >= approval) || wins;
        if (!reachesFile && !reachesApproval)
            return Result.Ok();

        var (kycRequired, tier, lineId) = await RequirementOfAsync(project, ct);
        var lead = project.LeadId is { } leadId
            ? await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;
        var clientId = project.CustomerEntityId ?? lead?.ConvertedToCustomerId ?? lead?.CustomerId;

        if (reachesApproval)
        {
            if (clientId is null)
                return Result.Fail(ProjectTemplateErrors.ClientFileRequired);

            return kycRequired ? await KycApprovedAsync(clientId.Value, tier, ct) : Result.Ok();
        }

        // The client-file step, below the approval one. Nothing to ask of a no-KYC product: its
        // client is created when the project is approved.
        if (!kycRequired)
            return Result.Ok();

        if (clientId is null)
            return Result.Fail(ProjectTemplateErrors.ClientFileRequired);

        // Crossing the step with a client that has no KYC file yet — a client created earlier on a
        // product that needed none, say: ask M02 for one, on this project's product line. The
        // consumer is idempotent, so a second crossing asks for nothing new.
        if (lead is not null && currentIndex < opens && await KycStatusAsync(clientId.Value, ct) is null or KycStatus.NotStarted)
        {
            await publisher.PublishAsync(
                new KycRequestedIntegrationEvent(
                    TenantId:         tenant.CurrentTenantId,
                    CustomerEntityId: clientId.Value,
                    LeadId:           lead.Id,
                    FullName:         lead.FullName,
                    PhoneNumber:      lead.PhoneNumber,
                    Email:            lead.Email,
                    NationalId:       lead.NationalId,
                    DateOfBirth:      lead.DateOfBirth,
                    RequestedBy:      currentUser.Id,
                    HasPepExposure:   lead.HasPepExposure,
                    ProductLineId:    lineId),
                ct);
        }

        return Result.Ok();
    }

    /// <summary>
    /// <list type="bullet">
    /// <item>Going FORWARD to a step — or past every step, winning — needs what the steps on the
    /// way ask of the project itself: its amount, its expected close date.</item>
    /// <item>Leaving a step forward needs its checklist ticked and its validation circuit
    /// approved. Every step between the current one and the target is left, so a step skipped on
    /// the way still owes its points and its circuit — which is what keeps « Marquer gagné » from
    /// jumping over the committee.</item>
    /// </list>
    /// Moving BACK needs nothing: it is how an agent reopens a step.
    /// </summary>
    private async Task<Result> ConditionsMetAsync(
        Opportunity project, List<ProjectStepDto> steps, int currentIndex, int targetIndex, CancellationToken ct)
    {
        if (targetIndex <= currentIndex)
            return Result.Ok();

        var reached = steps.Take(Math.Min(targetIndex + 1, steps.Count)).ToList();
        if (reached.Any(s => s.RequiresAmount) && (project.EstimatedAmount?.Amount ?? 0) <= 0)
            return Result.Fail(ProjectTemplateErrors.AmountRequired);
        if (reached.Any(s => s.RequiresCloseDate) && project.ExpectedCloseDate is null)
            return Result.Fail(ProjectTemplateErrors.CloseDateRequired);

        var left = steps.Skip(Math.Max(currentIndex, 0)).Take(targetIndex - Math.Max(currentIndex, 0)).ToList();

        var ticked = project.CheckedChecklistItems.ToHashSet();
        if (left.Any(s => (s.Checklist ?? []).Any(i => !ticked.Contains(i.Id))))
            return Result.Fail(ProjectTemplateErrors.ChecklistIncomplete);

        foreach (var step in left.Where(s => s.WorkflowCircuit is not null))
        {
            // Fail-closed: without the module there is no way to know the circuit approved.
            var state = workflows is null
                ? ProjectWorkflowState.Unknown
                : await workflows.StateAsync(project, step.Id, ct);

            switch (state)
            {
                case ProjectWorkflowState.Approved:
                    continue;
                case ProjectWorkflowState.Refused:
                    return Result.Fail(ProjectTemplateErrors.WorkflowRejected);
                case ProjectWorkflowState.Unknown:
                    return Result.Fail(ProjectTemplateErrors.WorkflowUnavailable);
                default:
                    return Result.Fail(ProjectTemplateErrors.WorkflowPending);
            }
        }

        return Result.Ok();
    }

    private async Task<Result> QuestionnairesAnsweredAsync(
        Opportunity project, bool includeDossier, CancellationToken ct)
    {
        try
        {
            var lead = await questionnaires!.LeadOfAsync(project, ct);
            var missing = await questionnaires.MissingAsync(project, lead, ct);
            var blocking = missing.Where(m => includeDossier || m.SubjectType != nameof(QualificationSubjectType.Lead));
            return blocking.Any() ? Result.Fail(ProjectTemplateErrors.QuestionnairesIncomplete) : Result.Ok();
        }
        catch (ProductLineUnavailableException)
        {
            // What the product asks is unknown: not known to be answered.
            return Result.Fail(ProjectTemplateErrors.QuestionnairesIncomplete);
        }
    }

    private async Task<Result> KycApprovedAsync(Guid clientId, string? tier, CancellationToken ct)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var status = await kyc.GetStatusAsync(tenant.CurrentTenantId, clientId, ct);
            if (status != KycStatus.Approved)
                return Result.Fail(ProjectTemplateErrors.KycNotApproved);

            if (tier is not null)
            {
                var limits = await kyc.GetLimitsAsync(tenant.CurrentTenantId, clientId, ct);
                if (limits is null || !string.Equals(limits.Tier, tier, StringComparison.OrdinalIgnoreCase))
                    return Result.Fail(ProjectTemplateErrors.KycTierInsufficient);
            }

            suppressed.Complete();
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return Result.Fail(ProjectTemplateErrors.KycNotApproved);
        }
    }

    private async Task<KycStatus?> KycStatusAsync(Guid clientId, CancellationToken ct)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            var status = await kyc.GetStatusAsync(tenant.CurrentTenantId, clientId, ct);
            suppressed.Complete();
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            // Unknown: asking for a file costs nothing when one exists (the consumer is idempotent).
            return null;
        }
    }

    /// <summary>(KYC required?, tier to compare or null, product line) for a project's product.</summary>
    private async Task<(bool Required, string? Tier, Guid? LineId)> RequirementOfAsync(Opportunity project, CancellationToken ct)
    {
        if (project.ProductLineId is null && string.IsNullOrWhiteSpace(project.Product))
            return (true, null, null);

        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            // The line recorded with the product; resolved again for a project older than it.
            var lineId = project.ProductLineId
                ?? await administration.ResolveProductLineAsync(tenant.CurrentTenantId, project.Product, ct);
            var policy = lineId is { } id
                ? await administration.GetBeneficiaryPolicyAsync(tenant.CurrentTenantId, id, ct)
                : null;

            suppressed.Complete();

            if (policy is null)
                return (true, null, lineId);

            return (KycRequirement.IsRequired(policy.RequiredKycTier), KycRequirement.Tier(policy.RequiredKycTier), lineId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return (true, null, null);
        }
    }
}
