namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateQualificationTemplateCommand(
    Guid TenantId,
    string Name,
    string? Description,
    string? ProductName,
    IReadOnlyList<QuestionInput> Questions
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "QualificationTemplate";
    public string? ResourceId => null;
}

/// <param name="Label">Question text shown to the agent.</param>
/// <param name="Type">Answer type — drives how the value is validated and scored.</param>
/// <param name="Weight">Points contributed when the answer is positive (any positive integer; normalised across all questions).</param>
/// <param name="IsRequired">When true the agent must provide an answer before submitting.</param>
/// <param name="Options">Allowed option labels for <c>SingleChoice</c> / <c>MultiChoice</c> questions.</param>
public sealed record QuestionInput(
    string Label,
    QuestionType Type,
    int Weight,
    bool IsRequired = false,
    string[]? Options = null);
