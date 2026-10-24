namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Moving a project onto a step of its template — ONE path for the step endpoint and the legacy
/// phase endpoint, which used to skip every check: the gate (markers, step conditions, circuits),
/// then the circuit the step starts, then the move, then the lead's conversion at approval. The
/// caller saves.
/// </summary>
internal sealed class ProjectStepTransition(
    TimeProvider clock,
    ProjectStepGate? gate = null,
    ProjectLeadConversion? conversion = null,
    ProjectWorkflows? workflows = null)
{
    public async Task<Result> MoveAsync(
        Opportunity project, ResolvedProjectTemplate template, ProjectStepDto step, CancellationToken ct)
    {
        var steps = template.Steps.ToList();
        var current = template.CurrentStep(project.StepId, project.Stage);
        var currentIndex = current is null ? -1 : steps.FindIndex(s => s.Id == current.Id);
        var targetIndex = steps.FindIndex(s => s.Id == step.Id);

        if (gate is not null)
        {
            var allowed = await gate.CheckAsync(project, template, currentIndex, targetIndex, ct);
            if (allowed.IsFailure)
                return allowed;
        }

        // Started BEFORE the move: a circuit that cannot start (deactivated since the step was
        // linked) refuses the move instead of leaving the project on a step nobody can validate.
        if (workflows is not null && step.WorkflowCircuit is not null && targetIndex != currentIndex)
        {
            var started = await workflows.StartIfNeededAsync(project, step, ct);
            if (started.IsFailure)
                return started;
        }

        var moved = project.MoveToStep(step.Id, step.Phase, step.Probability, clock.GetUtcNow());
        if (moved.IsFailure)
            return moved;

        // Reaching the approval step converts the lead (product owner, 2026-10-24).
        if (conversion is not null)
            await conversion.ConvertIfApprovedAsync(project, template, targetIndex, ct);

        return Result.Ok();
    }
}
