namespace Sankore.Modules.Leads.Features.SlaConfigs;

public sealed record SlaConfigDto(
    Guid Id,
    Guid? AgencyId,
    string Name,
    TimeSpan FirstContactDeadline,
    TimeSpan QualificationDeadline,
    TimeSpan FollowUpDeadline,
    TimeSpan EscalationDeadline,
    bool IsActive,
    DateTimeOffset CreatedAt);
