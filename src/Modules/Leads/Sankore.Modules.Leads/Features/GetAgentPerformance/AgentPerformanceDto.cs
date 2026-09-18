namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

public sealed record AgentPerformanceDto(
    Guid AgentId,
    int TotalAssigned,
    int ContactedWithinSla,
    int ContactedLate,
    int NotContacted,
    double SlaComplianceRate,
    double? AvgFirstContactMinutes,
    int ConvertedLeads);
