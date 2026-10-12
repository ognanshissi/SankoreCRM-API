namespace Sankore.Modules.Leads.Features.GetMyPerformance;

/// <summary>Onglet Performance de « Mon compte » : l'agent, comparé à son agence sur la même période.</summary>
public sealed record MyPerformanceDto(
    /// <summary>month, 30d ou quarter.</summary>
    string Period,
    DateTimeOffset From,
    DateTimeOffset To,
    string? AgencyName,
    PerformanceFiguresDto Agent,
    /// <summary>Null quand l'agent n'est rattaché à aucune agence : il n'y a rien à comparer.</summary>
    PerformanceFiguresDto? Agency,
    List<WeeklyConversionsDto> Weekly,
    int ConversionsInPeriod,
    List<StepSlaDto> StepSla,
    int SlaTargetPercent,
    List<ProductPerformanceDto> Products);

/// <param name="LeadsHandled">
/// Leads affectés sur la période. Pour l'agence, la moyenne par agent — un total d'agence
/// comparé au chiffre d'une personne ne dirait rien.
/// </param>
/// <param name="ConversionRate">Part des leads traités devenus clients, en pourcentage ; null sans lead.</param>
/// <param name="AvgFirstContactMinutes">Délai moyen entre l'affectation et le premier contact ; null sans contact.</param>
/// <param name="DeadlinesRespectedRate">Part des tâches échues terminées à temps, en pourcentage ; null sans tâche échue.</param>
public sealed record PerformanceFiguresDto(
    double LeadsHandled,
    double? ConversionRate,
    double? AvgFirstContactMinutes,
    double? DeadlinesRespectedRate);

/// <param name="Label">« S41 » : la semaine ISO.</param>
public sealed record WeeklyConversionsDto(int Year, int Week, string Label, DateOnly StartsOn, int Conversions);

/// <param name="Rate">Pourcentage dans les délais ; null quand aucune étape n'a été close.</param>
public sealed record StepSlaDto(string Name, int Total, int OnTime, double? Rate);

/// <param name="AvgDaysToConvert">De l'affectation à la conversion, pour les seuls leads convertis.</param>
public sealed record ProductPerformanceDto(
    string Product, int Received, int Converted, double? Rate, double? AvgDaysToConvert);
