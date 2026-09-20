namespace Sankore.Modules.Leads.Features.PipelineStages;

public sealed record PipelineStageConfigDto(
    Guid Id,
    string Code,
    string Label,
    string? Description,
    int DisplayOrder,
    string? Color,
    bool IsActive,
    bool IsSystem,
    bool IsFinal,
    DateTimeOffset CreatedAt);
