namespace Sankore.Modules.Leads.Features.TaskTypes;

public sealed record TaskTypeDto(
    Guid Id,
    string Code,
    string Label,
    string? Description,
    bool IsActive,
    bool IsSystem,
    int DisplayOrder,
    DateTimeOffset CreatedAt);
