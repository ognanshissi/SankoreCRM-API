using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Features.Reports.GetLeadReport;

public enum ReportGranularity { Day, Week, Month }

/// <summary>
/// Everything the administrators' report screen shows, in ONE response: the screen would
/// otherwise fan out to five endpoints whose date filters do not even apply to the same column
/// (capture date, assignment date, ingestion date), and its figures would disagree with each other.
/// </summary>
/// <param name="From">Inclusive start, UTC.</param>
/// <param name="To">Exclusive end, UTC.</param>
public sealed record LeadReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    ReportGranularity Granularity,
    LeadReportKpis Kpis,
    IReadOnlyList<LeadReportPoint> Series,
    IReadOnlyList<LeadReportStage> Progression,
    IReadOnlyList<LeadReportStatus> ByStatus,
    IReadOnlyList<LeadReportSource> BySource,
    IReadOnlyList<LeadReportAgent> Agents,
    IReadOnlyList<LeadReportAgency> Agencies,
    /// <param name="Projects">
    /// Projects opened in the period, by the phase they are in today — open phases, then won and
    /// lost. Under an agency filter, only the projects of that agency's leads.
    /// </param>
    IReadOnlyList<LeadReportProjectPhase>? Projects = null,
    /// <param name="Forecast">
    /// What the open projects should bring in during the period — see <see cref="LeadReportForecast"/>.
    /// </param>
    LeadReportForecast? Forecast = null);

/// <param name="Amounts">
/// The estimated amounts, one line per kind of amount present — a loan and a deposit are not
/// added together. All currencies added as they are.
/// </param>
public sealed record LeadReportProjectPhase(OpportunityStage Phase, int Count, IReadOnlyList<LeadReportKindAmount> Amounts);

public sealed record LeadReportKindAmount(ProjectAmountKind Kind, decimal Amount);

/// <summary>
/// The sales forecast: OPEN projects whose expected close date falls in the period (or has already
/// passed), on templates that track a forecast — a product sold in a meeting tracks none.
///
/// <para>
/// <b>Never one total.</b> Every figure is per <see cref="ProjectAmountKind"/>: a loan disbursed and
/// a deposit collected are flows in opposite directions, and their sum is a number nobody can plan
/// with. Under an agency filter, the projects of that agency — the lead's, else the client's.
/// </para>
/// </summary>
/// <param name="Kinds">One line per kind of amount that has a project to show.</param>
/// <param name="Schedule">Month by month over the period, per kind: what treasury plans with.</param>
/// <param name="Agencies">Per agency and kind, every agency side by side.</param>
public sealed record LeadReportForecast(
    IReadOnlyList<LeadReportForecastKind> Kinds,
    IReadOnlyList<LeadReportForecastMonth> Schedule,
    IReadOnlyList<LeadReportForecastAgency> Agencies);

/// <param name="Count">Projects expected to close in the period.</param>
/// <param name="Amount">Their estimated amounts, all currencies added as they are.</param>
/// <param name="WeightedAmount">The same, each × its probability: the expected signature.</param>
/// <param name="Overdue">Open projects whose expected close date has passed — to chase.</param>
/// <param name="OverdueWeightedAmount">Their weighted amount.</param>
public sealed record LeadReportForecastKind(
    ProjectAmountKind Kind, int Count, decimal Amount, decimal WeightedAmount, int Overdue, decimal OverdueWeightedAmount);

/// <param name="Month">First day of the month (UTC). Every month of the period is present, zeros included.</param>
public sealed record LeadReportForecastMonth(
    DateOnly Month, ProjectAmountKind Kind, int Count, decimal Amount, decimal WeightedAmount);

/// <param name="AgencyId">Null groups the projects attached to no agency.</param>
public sealed record LeadReportForecastAgency(
    Guid? AgencyId, string Name, ProjectAmountKind Kind, int Count, decimal WeightedAmount, int Overdue, decimal OverdueWeightedAmount);

/// <param name="Captured">Leads whose <c>CapturedAt</c> falls in the period.</param>
/// <param name="ConvertedInPeriod">
/// Conversions that HAPPENED in the period (<c>ConvertedAt</c>), whatever the capture date — the
/// activity figure. Not to be divided by <paramref name="Captured"/>: that is what
/// <paramref name="CohortConversionRate"/> is for.
/// </param>
/// <param name="CohortConversionRate">
/// Of the leads captured in the period, the share converted today, 0–100. A cohort rate: a recent
/// period reads low because its leads have not had time to convert yet.
/// </param>
/// <param name="OpenLeads">Leads not in a terminal status today, all capture dates.</param>
/// <param name="SlaComplianceRate">
/// First contacts made before the deadline, over the assignments created in the period — the same
/// definition as <c>GET leads/agent-performance</c>. Null when there was no assignment.
/// </param>
/// <param name="OpenSlaBreaches">Assignments overdue for a first contact RIGHT NOW.</param>
public sealed record LeadReportKpis(
    int Captured,
    int ConvertedInPeriod,
    double CohortConversionRate,
    int Lost,
    int OpenLeads,
    double? AvgFirstContactMinutes,
    double? SlaComplianceRate,
    int OpenSlaBreaches);

public sealed record LeadReportPoint(DateTimeOffset PeriodStart, int Captured, int Converted);

/// <summary>
/// Leads captured in the period, by the pipeline stage they are in today — the contact stages and
/// the two outcomes (<see cref="LeadPipeline.ReportStages"/>); the application stages are projects.
/// </summary>
public sealed record LeadReportStage(PipelineStage Stage, int Count);

public sealed record LeadReportStatus(LeadStatus Status, int Count);

/// <param name="Converted">Of those captured in the period, how many are converted today.</param>
public sealed record LeadReportSource(LeadSource Source, int Captured, int Converted, double ConversionRate);

/// <param name="Assigned">Assignments the agent received in the period (re-assignments taken away before any contact excluded).</param>
/// <param name="Converted">Leads converted in the period while assigned to the agent.</param>
/// <param name="ConversionRate">
/// Of the distinct leads assigned to the agent in the period, the share converted today while still
/// theirs — the same cohort rate as the agent sheet (<c>GET leads/reports/agents</c>).
/// </param>
/// <param name="OpenLeads">The agent's current workload: non-terminal leads assigned to them today.</param>
public sealed record LeadReportAgent(
    Guid AgentId,
    string Name,
    int Assigned,
    int ContactedWithinSla,
    int ContactedLate,
    int NotContacted,
    double? SlaComplianceRate,
    double? AvgFirstContactMinutes,
    int Converted,
    double ConversionRate,
    int OpenLeads);

/// <param name="AgencyId">Null groups the leads attached to no agency.</param>
public sealed record LeadReportAgency(Guid? AgencyId, string Name, int Captured, int Converted, double ConversionRate);
