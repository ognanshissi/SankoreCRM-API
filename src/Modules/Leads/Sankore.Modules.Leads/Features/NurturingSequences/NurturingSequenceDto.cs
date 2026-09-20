namespace Sankore.Modules.Leads.Features.NurturingSequences;

public sealed record NurturingSequenceDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NurturingStepDto> Steps);

public sealed record NurturingStepDto(
    Guid Id,
    int Order,
    TimeSpan DelayFromPrevious,
    string EmailTemplateKey,
    string? Subject);
