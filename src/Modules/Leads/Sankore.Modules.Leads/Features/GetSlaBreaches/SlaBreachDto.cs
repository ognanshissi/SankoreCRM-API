namespace Sankore.Modules.Leads.Features.GetSlaBreaches;

public sealed record SlaBreachDto(
    Guid LeadId,
    string FullName,
    string PhoneNumber,
    Guid AgentId,
    Guid AssignmentId,
    DateTimeOffset AssignedAt,
    DateTimeOffset SlaDeadline,
    double BreachHours);
