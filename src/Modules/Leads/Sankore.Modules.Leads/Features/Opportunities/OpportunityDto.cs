namespace Sankore.Modules.Leads.Features.Opportunities;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <param name="Product">Empty while nobody has matched the need to a product.</param>
public sealed record OpportunityDto(
    Guid Id,
    Guid? LeadId,
    string? CustomerEntityType,
    Guid? CustomerEntityId,
    string Title,
    string? Description,
    string Product,
    Money? EstimatedAmount,
    OpportunityStage Stage,
    double Probability,
    DateTimeOffset? ExpectedCloseDate,
    Guid? OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    string? NextStep = null,
    DateOnly? NextStepDueOn = null,
    /// <param name="SeparateNeedConfirmedAt">
    /// When an agent said this request is a different need from the lead's, despite the amounts.
    /// </param>
    DateTimeOffset? SeparateNeedConfirmedAt = null,
    /// <param name="StepId">The step stored on the project; null when it was moved by phase only.</param>
    Guid? StepId = null,
    /// <param name="TemplateId">The template its product resolves to; null for the built-in default.</param>
    Guid? TemplateId = null,
    string? TemplateName = null,
    /// <param name="CurrentStepId">
    /// The step it stands on, as resolved — <see cref="StepId"/> when the template still has it,
    /// else the step of its phase. Null once closed.
    /// </param>
    Guid? CurrentStepId = null,
    /// <param name="Steps">The steps of its template, in order: what the screens draw.</param>
    IReadOnlyList<ProjectTemplates.ProjectStepDto>? Steps = null,
    /// <param name="Contact">Who the project is for — filled on the list, for the board's search.</param>
    ProjectContactDto? Contact = null,
    /// <param name="TracksForecast">
    /// Its template shows a probability and an expected close date; off for products sold in a
    /// meeting or two, where the screens hide both.
    /// </param>
    bool TracksForecast = true,
    /// <param name="AmountKind">What its amount stands for: a loan, a premium, a deposit — or nothing particular.</param>
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    /// <param name="CriticalCloseDate">Its close date is a deadline: required, and chased once passed.</param>
    bool CriticalCloseDate = false,
    /// <param name="CheckedChecklistItemIds">The checklist points of its steps already ticked.</param>
    IReadOnlyList<Guid>? CheckedChecklistItemIds = null,
    /// <param name="WorkflowRuns">
    /// The validation circuits started for its steps, latest last. <c>State</c> is read from M12 on
    /// the single-project read only; a list leaves it null rather than call M12 once per row.
    /// </param>
    IReadOnlyList<ProjectWorkflowRunDto>? WorkflowRuns = null,
    /// <param name="Fields">The fields of its template; a step's RequiredFieldKeys name some of them.</param>
    IReadOnlyList<ProjectFieldDefinition>? Fields = null,
    /// <param name="CustomFields">Their values, keyed by field key — files are in Attachments.</param>
    System.Text.Json.JsonElement? CustomFields = null,
    IReadOnlyList<ProjectFields.ProjectAttachmentDto>? Attachments = null);

/// <param name="State">NotStarted, InProgress, Approved, Refused or Unknown — null when not read.</param>
public sealed record ProjectWorkflowRunDto(
    Guid StepId, string Circuit, Guid InstanceId, DateTimeOffset StartedAt, ProjectTemplates.ProjectWorkflowState? State);

/// <summary>
/// The person behind a project, as the projects board shows and searches it: the lead's name and
/// coordinates when the project came from a lead, else the client's name and number. A client's
/// phone and e-mail are not here: M01 keeps them encrypted and exposes no contract that reveals
/// them to a list.
/// </summary>
public sealed record ProjectContactDto(
    string Name,
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? Email,
    string? ClientNumber);

internal static class OpportunityStepEnrichment
{
    /// <summary>
    /// Adds the ticked checklist points and the circuit runs — one query for every row. With
    /// <paramref name="workflows"/>, also reads the state of each step's LATEST run from M12.
    /// </summary>
    public static async Task<IReadOnlyList<OpportunityDto>> WithConditionsAsync(
        this IReadOnlyList<OpportunityDto> items,
        Infrastructure.LeadsDbContext db,
        ProjectTemplates.ProjectWorkflows? workflows,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return items;

        var ids = items.Select(o => o.Id).ToList();
        var projects = await db.Opportunities.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync(ct);
        var byId = projects.ToDictionary(o => o.Id);
        var files = (await db.ProjectAttachments.AsNoTracking()
                .Where(a => ids.Contains(a.OpportunityId) && a.DeletedAt == null)
                .OrderBy(a => a.UploadedAt)
                .ToListAsync(ct))
            .ToLookup(a => a.OpportunityId);

        var result = new List<OpportunityDto>(items.Count);
        foreach (var item in items)
        {
            if (!byId.TryGetValue(item.Id, out var project))
            {
                result.Add(item);
                continue;
            }

            var runs = new List<ProjectWorkflowRunDto>();
            foreach (var run in project.WorkflowRuns.OrderBy(r => r.StartedAt))
            {
                var latest = project.LatestWorkflowRun(run.StepId) == run;
                var state = workflows is not null && latest
                    ? await workflows.StateAsync(project, run.StepId, ct)
                    : (ProjectTemplates.ProjectWorkflowState?)null;
                runs.Add(new ProjectWorkflowRunDto(run.StepId, run.Circuit, run.InstanceId, run.StartedAt, state));
            }

            result.Add(item with
            {
                CheckedChecklistItemIds = project.CheckedChecklistItems,
                WorkflowRuns = runs,
                CustomFields = project.CustomFieldsJson is null
                    ? null
                    : System.Text.Json.JsonDocument.Parse(project.CustomFieldsJson).RootElement.Clone(),
                Attachments = files[project.Id].Select(ProjectFields.ProjectAttachmentHandlers.ToDto).ToList(),
            });
        }
        return result;
    }

    /// <summary>Adds each project's template and current step. One resolution per distinct product.</summary>
    public static async Task<IReadOnlyList<OpportunityDto>> WithStepsAsync(
        this IEnumerable<OpportunityDto> items,
        ProjectTemplates.ProjectTemplateResolver resolver,
        CancellationToken ct)
    {
        var result = new List<OpportunityDto>();
        foreach (var item in items)
        {
            var template = await resolver.ResolveAsync(item.Product, ct);
            result.Add(item with
            {
                TemplateId    = template.Id,
                TemplateName  = template.Name,
                CurrentStepId = template.CurrentStep(item.StepId, item.Stage)?.Id,
                Steps         = template.Steps,
                TracksForecast = template.TracksForecast,
                AmountKind     = template.AmountKind,
                CriticalCloseDate = template.CriticalCloseDate,
                Fields         = template.Fields ?? [],
            });
        }
        return result;
    }
}
