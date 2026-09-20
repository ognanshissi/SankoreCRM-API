namespace Sankore.Modules.Leads.Features.LeadSources;

public sealed record LeadSourceDto(
    Guid Id,
    Guid TenantId,
    string Code,
    string Label,
    string? Description,
    bool IsActive,
    bool IsSystem,
    int DisplayOrder,
    DateTimeOffset CreatedAt);
