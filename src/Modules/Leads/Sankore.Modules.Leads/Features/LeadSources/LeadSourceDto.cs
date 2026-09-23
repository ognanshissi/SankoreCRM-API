namespace Sankore.Modules.Leads.Features.LeadSources;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>List-level DTO with computed health, volume and last-received.</summary>
public sealed record LeadSourceListDto(
    Guid Id,
    string Code,
    string Label,
    string? Description,
    LeadChannelType ChannelType,
    IntegrationMode Mode,
    LeadSourceStatus Status,
    SourceHealth Health,
    DateTimeOffset? LastReceivedAt,
    int Volume7Days,
    Money? CostPerLead,
    bool IsSystem,
    int DisplayOrder,
    uint Version,
    DateTimeOffset CreatedAt);

/// <summary>Detail-level DTO with settings, field mapping, consent policy and secret hints.</summary>
public sealed record LeadSourceDetailDto(
    Guid Id,
    string Code,
    string Label,
    string? Description,
    LeadChannelType ChannelType,
    IntegrationMode Mode,
    LeadSourceStatus Status,
    SourceHealth Health,
    DateTimeOffset? LastReceivedAt,
    int Volume7Days,
    string? PublicKey,
    SourceSettings? Settings,
    string? PlatformConnectionId,
    int DedupWindowDays,
    Money? CostPerLead,
    bool IsSystem,
    int DisplayOrder,
    uint Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SecretHintDto> Secrets);

/// <summary>Secret metadata exposed without the actual value.</summary>
public sealed record SecretHintDto(
    string Name,
    string? Hint,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ExpiresAt);

public enum SourceHealth
{
    Ok,
    Stale,
    Error
}
