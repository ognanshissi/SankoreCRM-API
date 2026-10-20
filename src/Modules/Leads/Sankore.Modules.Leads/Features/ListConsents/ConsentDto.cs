namespace Sankore.Modules.Leads.Features.ListConsents;

/// <param name="Status"><c>Active</c>, <c>Withdrawn</c>, <c>Expired</c>, or <c>Refused</c> — the prospect said no.</param>
/// <param name="GrantedAt">When the answer was recorded, accepted or refused.</param>
/// <param name="ProofActivityId">The logged call or visit during which the prospect answered.</param>
/// <param name="SubjectType">Null for a lead-wide consent; set for a per-person one (health).</param>
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
    string? WithdrawalReason,
    Guid? ProofActivityId = null,
    string? SubjectType = null,
    Guid? SubjectId = null);
