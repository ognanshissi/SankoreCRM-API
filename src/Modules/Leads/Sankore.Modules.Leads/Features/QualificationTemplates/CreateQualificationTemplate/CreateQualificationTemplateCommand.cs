namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateQualificationTemplateCommand(
    Guid TenantId,
    string Name,
    string? Description,
    string? ProductCategory,
    string? ProductCode,
    /// <summary>Flat question list. Mutually exclusive with <see cref="Sections"/>.</summary>
    IReadOnlyList<QuestionInput>? Questions,
    /// <summary>Section-grouped questions. Mutually exclusive with <see cref="Questions"/>.</summary>
    IReadOnlyList<SectionInput>? Sections
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => null;
}

/// <param name="Label">Question text shown to the agent.</param>
/// <param name="Type">Answer type — drives how the value is validated and scored.</param>
/// <param name="Weight">Points contributed when the answer is positive (positive integer; normalised across all questions).</param>
/// <param name="IsRequired">When true the agent must provide an answer before submitting.</param>
/// <param name="Options">Allowed option labels for <c>SingleChoice</c> / <c>MultiChoice</c> questions.</param>
/// <param name="HelpText">Short guidance shown beneath the question label.</param>
/// <param name="PlaceholderText">Input placeholder for Text and Numeric questions.</param>
/// <param name="MinValue">Minimum value for proportional Numeric scoring.</param>
/// <param name="MaxValue">Maximum value for proportional Numeric scoring.</param>
/// <param name="Rules">Conditional visibility / requirement rules.</param>
public sealed record QuestionInput(
    string Label,
    QuestionType Type,
    int Weight,
    bool IsRequired = false,
    string[]? Options = null,
    string? HelpText = null,
    string? PlaceholderText = null,
    decimal? MinValue = null,
    decimal? MaxValue = null,
    IReadOnlyList<RuleInput>? Rules = null);

/// <param name="Title">Section heading shown to the agent.</param>
/// <param name="Description">Optional section sub-heading.</param>
/// <param name="Questions">Questions belonging to this section, in display order.</param>
public sealed record SectionInput(
    string Title,
    string? Description = null,
    IReadOnlyList<QuestionInput>? Questions = null);

/// <param name="TriggerQuestionId">ID of the question whose answer drives this rule.</param>
/// <param name="TriggerValue">Answer value that satisfies the trigger condition.</param>
/// <param name="Action">What to do when the trigger fires.</param>
public sealed record RuleInput(
    Guid TriggerQuestionId,
    string TriggerValue,
    QuestionRuleAction Action);
