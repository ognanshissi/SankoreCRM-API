namespace Sankore.Modules.Leads.Features.ListConsents;

public sealed record ConsentDto(
    Guid Id,
    Guid LeadId,
    string Type,
    string Channel,
    string Status,
    DateTimeOffset GrantedAt,
    string? ProofReference,
    Guid RecordedBy,
    DateTimeOffset? WithdrawnAt,
    Guid? WithdrawnBy,
    string? WithdrawalReason);
