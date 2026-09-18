namespace Sankore.Modules.Leads.Features.ListDismissals;

public sealed record DismissalDto(
    Guid Id,
    Guid LeadId,
    Guid CandidateLeadId,
    Guid DismissedBy,
    DateTimeOffset DismissedAt,
    string? Reason);
