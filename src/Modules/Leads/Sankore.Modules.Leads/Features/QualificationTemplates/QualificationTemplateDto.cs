namespace Sankore.Modules.Leads.Features.QualificationTemplates;

public sealed record QualificationTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    string? ProductCategory,
    string? ProductCode,
    string Status,
    int Version,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<QualificationSectionDto> Sections,
    IReadOnlyList<QualificationQuestionDto> Questions,
    /// <summary>Who it is answered about: Lead (the file), EachInsuredPerson, PrincipalOnly, SpouseOnly, ChildrenOnly.</summary>
    string SubjectScope = "Lead",
    /// <summary>Its answers are health data: encrypted, never scored, behind the health consent.</summary>
    bool IsHealthQuestionnaire = false,
    /// <summary>Its answers do not weigh in the score — always true for a health questionnaire.</summary>
    bool ExcludeFromScoring = false);

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
