namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Puts a new project on the FIRST step of its template. A project was created on the legacy
/// « Prospection » phase at 10 %, with no step: on a template starting at « Recueil du besoin »
/// (Qualification, 20 %) the board filed it under Prospection and the page showed 10 % « fixée par
/// l'étape » beside a step that says 20 %.
/// </summary>
/// <remarks>
/// An ENTRY, not a move: the gate's « to reach » conditions and a circuit linked to the first step
/// are not run here — a project has to exist before anything can be asked of it, and the step move
/// that follows checks everything as usual.
/// </remarks>
internal static class ProjectFirstStep
{
    public static async Task PlaceAsync(
        ProjectTemplateResolver? templates, Opportunity project, DateTimeOffset now, CancellationToken ct)
    {
        if (templates is null) return;

        var first = (await templates.ResolveAsync(project.Product, ct)).Steps.FirstOrDefault();
        if (first is not null)
            project.MoveToStep(first.Id, first.Phase, first.Probability, now);
    }
}
