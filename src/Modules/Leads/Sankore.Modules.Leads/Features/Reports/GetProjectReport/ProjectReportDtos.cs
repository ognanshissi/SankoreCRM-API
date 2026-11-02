using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Features.Reports.GetProjectReport;

/// <summary>The « Projets » tab of the reports, in one response.</summary>
/// <param name="From">Inclusive start, UTC.</param>
/// <param name="To">Exclusive end, UTC.</param>
/// <param name="Previous">The period figures over the period of the same length just before.</param>
/// <param name="Pipeline">The projects open today, per kind of amount — never one total.</param>
/// <param name="ByProduct">The projects opened in the period, by product, where they stand today.</param>
/// <param name="LossReasons">The most frequent reasons of the projects lost in the period, as typed.</param>
/// <param name="Steps">Time spent per step of each template, slowest first.</param>
/// <param name="Overdue">Open projects past their closing date, the most late first.</param>
public sealed record ProjectReportDto(
    DateTimeOffset From,
    DateTimeOffset To,
    ProjectReportKpis Kpis,
    ProjectReportPeriodKpis Previous,
    IReadOnlyList<ProjectReportPipeline> Pipeline,
    IReadOnlyList<ProjectReportProduct> ByProduct,
    IReadOnlyList<ProjectReportLossReason> LossReasons,
    IReadOnlyList<ProjectReportStep> Steps,
    IReadOnlyList<ProjectReportOverdue> Overdue);

/// <param name="Period">The figures of the period, compared with <see cref="ProjectReportDto.Previous"/>.</param>
/// <param name="OpenProjects">Open today, whatever their opening date.</param>
/// <param name="Overdue">Open today and past their expected closing date.</param>
public sealed record ProjectReportKpis(ProjectReportPeriodKpis Period, int OpenProjects, int Overdue);

/// <param name="Opened">Projects created in the period.</param>
/// <param name="Won">Projects won in the period, whatever their opening date.</param>
/// <param name="Lost">Projects lost in the period.</param>
/// <param name="WinRate">Won ÷ (won + lost) in the period, in percent; null when none was closed.</param>
/// <param name="WonAmount">Estimated amounts of the projects won, all kinds added — shown with care.</param>
/// <param name="AvgCycleDays">Average days from opening to won, for the projects won in the period.</param>
public sealed record ProjectReportPeriodKpis(
    int Opened, int Won, int Lost, double? WinRate, decimal WonAmount, double? AvgCycleDays);

/// <param name="Weighted">Amount × the probability of the project's step.</param>
public sealed record ProjectReportPipeline(ProjectAmountKind Kind, int Count, decimal Amount, decimal Weighted);

public sealed record ProjectReportProduct(string Product, int Opened, int Won, int Lost, double? WinRate, decimal Amount);

public sealed record ProjectReportLossReason(string Reason, int Count);

/// <param name="Template">The project template the step belongs to.</param>
/// <param name="Left">Projects that left the step in the period.</param>
/// <param name="AvgDays">Average days those projects spent in it.</param>
/// <param name="WaitingNow">Open projects in the step today.</param>
/// <param name="AvgWaitingDays">How long they have been there, on average.</param>
public sealed record ProjectReportStep(
    string Template, string Step, int Left, double? AvgDays, int WaitingNow, double? AvgWaitingDays);

public sealed record ProjectReportOverdue(
    Guid ProjectId,
    string Title,
    string Product,
    OpportunityStage Phase,
    string? Step,
    Guid? OwnerId,
    string? OwnerName,
    DateTimeOffset ExpectedCloseDate,
    int DaysLate,
    decimal Amount);
