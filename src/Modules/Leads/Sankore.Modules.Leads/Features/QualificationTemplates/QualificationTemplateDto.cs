namespace Sankore.Modules.Leads.Features.QualificationTemplates;

public sealed record QualificationTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    string? ProductName,
    string Status,
    int Version,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<QualificationSectionDto> Sections,
    IReadOnlyList<QualificationQuestionDto> Questions);

public sealed record QualificationSectionDto(
    Guid Id,
    string Title,
    string? Description,
    int Order);

public sealed record QualificationQuestionDto(
    Guid Id,
    Guid? SectionId,
    string Label,
    string? HelpText,
    string? PlaceholderText,
    string Type,
    string[] Options,
    int Weight,
    bool IsRequired,
    int Order,
    decimal? MinValue,
    decimal? MaxValue,
    IReadOnlyList<QuestionRuleDto> Rules);

public sealed record QuestionRuleDto(
    Guid TriggerQuestionId,
    string TriggerValue,
    string Action);
