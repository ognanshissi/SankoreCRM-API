namespace Sankore.Modules.Leads.Features.Opportunities;

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
    bool TracksForecast = true);

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
            });
        }
        return result;
    }
}
