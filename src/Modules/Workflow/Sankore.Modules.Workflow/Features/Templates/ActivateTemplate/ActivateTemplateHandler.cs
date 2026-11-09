using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;

internal sealed class ActivateTemplateHandler(
    WorkflowDbContext db,
    IEnumerable<Sankore.Shared.Infrastructure.Workflow.IContextProvider>? contextProviders = null,
    IEnumerable<Sankore.Shared.Infrastructure.Workflow.IWorkflowRecordGate>? recordGates = null,
    ITenantModules? modules = null)
    : IRequestHandler<ActivateTemplateCommand, Result>
{
    public async Task<Result> Handle(ActivateTemplateCommand request, CancellationToken ct)
    {
        // Steps only: they are all Activate() reads to lay out the circuit, and the transition
        // rows are reconciled against the database further down rather than through the
        // navigation. Including Transitions here is now harmless — Activate() only removes the
        // auto-generated ones — but it buys nothing and makes two mechanisms responsible for the
        // same deletes.
        var template = await db.WorkflowTemplates
            .AsTracking()
            .Include(t => t.Steps)
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct);

        if (template is null)
            return Result.Fail($"Template {request.TemplateId} not found.");

        if (template.IsActive)
            return Result.Fail("Template is already active.");

        // Check no other template of the same circuit is already active
        var conflictExists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == template.EntityType
                        && t.Circuit == template.Circuit
                        && t.IsActive
                        && t.Id != template.Id, ct);

        if (conflictExists)
            return Result.Fail(
                "Une autre version de ce circuit est déjà active : désactivez-la avant d'activer celle-ci.");

        if (template.Steps.OrderBy(s => s.Order).FirstOrDefault(s => StepKinds.BlocksActivation(s.Kind)) is { } unsupported)
            return Result.Fail(
                $"L'étape « {unsupported.Name} » ({StepKinds.Label(unsupported.Kind)}) n'est pas encore exécutée par le moteur : " +
                "elle arrêterait le dossier. Retirez-la pour publier ce parcours.");

        // Conditions at any depth, and « Fin » steps (2026-11-02): the tree must hold together.
        if (StepTree.Problem(
                template.Steps.Select(st => new StepTree.Step(st.Id, st.Order, st.Kind, st.ConfigJson, st.Name)).ToList(),
                template.EntityType) is { } treeProblem)
            return Result.Fail(treeProblem);

        var provider = (contextProviders ?? []).FirstOrDefault(p =>
            string.Equals(p.EntityType, template.EntityType, StringComparison.OrdinalIgnoreCase));
        foreach (var condition in template.Steps.Where(s => s.Kind == StepKind.Condition).OrderBy(s => s.Order))
        {
            if (Infrastructure.Branching.ConditionStepConfig.Problem(condition.Name, condition.ConfigJson, provider) is { } problem)
                return Result.Fail(problem);
        }

        var hasGate = (recordGates ?? []).Any(g => string.Equals(g.EntityType, template.EntityType, StringComparison.OrdinalIgnoreCase));
        foreach (var step in template.Steps.OrderBy(s => s.Order))
        {
            var problem = step.Kind switch
            {
                StepKind.Form when !hasGate =>
                    $"L'étape « {step.Name} » (Formulaire) : ce type de dossier n'a pas de formulaire à vérifier.",
                StepKind.Form when Infrastructure.Gates.RecordStepGates.Fields(step.ConfigJson).Count == 0 =>
                    $"L'étape « {step.Name} » : choisissez les champs à remplir.",
                StepKind.QualificationForm when !hasGate || string.Equals(template.EntityType, "Account", StringComparison.OrdinalIgnoreCase) =>
                    $"L'étape « {step.Name} » (Formulaire de qualification) : ce type de dossier n'a pas de questionnaire.",
                StepKind.IntegrationAction when Infrastructure.Gates.RecordStepGates.IsInsurance(step.ConfigJson)
                    && !string.Equals(template.EntityType, "Opportunity", StringComparison.OrdinalIgnoreCase) =>
                    $"L'étape « {step.Name} » attend un contrat d'assurance : seuls les parcours Projet en ont un.",
                StepKind.IntegrationAction when !hasGate && !string.Equals(template.EntityType, "KycFile", StringComparison.OrdinalIgnoreCase) =>
                    $"L'étape « {step.Name} » (Action Integration) : ce type de dossier n'est relié à aucun client.",
                StepKind.KycCheck when !hasGate && !string.Equals(template.EntityType, "KycFile", StringComparison.OrdinalIgnoreCase) =>
                    $"L'étape « {step.Name} » (Vérification KYC) : ce type de dossier n'est relié à aucun client.",
                _ => null,
            };
            if (problem is not null) return Result.Fail(problem);
        }

        // A step waiting for the core banking or the insurer would hold the dossier for ever in an
        // institution without that module (docs/isolation-socle.md, lot 1).
        if (modules is not null)
        {
            foreach (var step in template.Steps.Where(s => s.Kind == StepKind.IntegrationAction).OrderBy(s => s.Order))
            {
                var insurance = Infrastructure.Gates.RecordStepGates.IsInsurance(step.ConfigJson);
                if (!await modules.IsEnabledAsync(insurance ? TenantModules.Insurance : TenantModules.BankingMicrofinance, ct))
                    return Result.Fail(insurance
                        ? $"L'étape « {step.Name} » attend l'assureur : le module « Assurance » n'est pas activé pour votre institution."
                        : $"L'étape « {step.Name} » attend le core banking : le module « Banking - Microfinance » n'est pas activé pour votre institution.");
            }
        }

        foreach (var wait in template.Steps.Where(s => s.Kind == StepKind.Wait).OrderBy(s => s.Order))
        {
            if (WaitStepConfig.Problem(wait.Name, wait.TimeoutHours, wait.ConfigJson, template.EntityType) is { } problem)
                return Result.Fail(problem);
        }

        // A notification step that would send nothing used to be published anyway: it then waited
        // for an approval nobody was asked for. It is franchie automatically now, so say it here.
        foreach (var step in template.Steps.Where(s => s.Kind == StepKind.Notification).OrderBy(s => s.Order))
        {
            if (NotificationStepConfig.Problem(step.Name, step.ConfigJson) is { } problem)
                return Result.Fail(problem);
        }

        try
        {
            template.Activate();
        }
        catch (Sankore.Shared.Kernel.DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        // Activate() rebuilt the auto-generated transitions with fresh Ids, so the previous
        // generation's rows have to go. Snapshot the new ones FIRST: loading the old rows below
        // makes EF relationship fix-up append them into template.Transitions, and reading that
        // navigation afterwards would hand them back — AddRange would flip them Deleted → Added
        // and SaveChanges would fail on a duplicate PK. With the snapshot taken, the deletes and
        // the inserts are two disjoint sets in one SaveChanges, and the non-auto-generated
        // transitions are never touched.
        var regenerated = template.Transitions.Where(t => t.IsAutoGenerated).ToList();

        // Not ExecuteDeleteAsync: it runs its DELETE immediately, outside SaveChanges, and the
        // EF InMemory provider the suite runs on does not implement it at all — which left the
        // whole activation path untested.
        var superseded = await db.WorkflowTransitions
            .AsTracking()
            .Where(t => t.TemplateId == template.Id && t.IsAutoGenerated)
            .ToListAsync(ct);

        db.WorkflowTransitions.RemoveRange(superseded);
        db.WorkflowTransitions.AddRange(regenerated);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
