namespace Sankore.Modules.Leads.Features.QualificationTemplates;

public sealed record QualificationTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    string? ProductName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<QualificationQuestionDto> Questions);

public sealed record QualificationQuestionDto(
    Guid Id,
    string Label,
    string Type,
    string[] Options,
    int Weight,
    bool IsRequired,
    int Order);
