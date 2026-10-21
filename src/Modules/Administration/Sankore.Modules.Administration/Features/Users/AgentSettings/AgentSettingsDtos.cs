namespace Sankore.Modules.Administration.Features.Users.AgentSettings;

public sealed record AgentSettingsDto(
    string Availability,
    AbsenceDto? Absence,
    /// <summary>Faux tant que l'agent n'a jamais enregistré d'horaires : la grille renvoyée est une proposition.</summary>
    bool WorkingHoursConfigured,
    List<WorkingDayDto> WorkingHours,
    int? MaxActiveLeads,
    int CapacityCeiling,
    int ActiveLeadsCount,
    List<NotificationPreferenceDto> NotificationPreferences,
    List<string> Products,
    string? AgencyName,
    List<string> SpokenLanguages,
    string? ManagerName,
    List<DelegationDto> Delegations,
    DateTimeOffset? UpdatedAt,
    bool NotificationSound = true);

public sealed record AbsenceDto(
    DateOnly From,
    DateOnly To,
    Guid? ReplacementUserId,
    string? ReplacementName,
    bool TransferTasks);

public sealed record WorkingDayDto(DayOfWeek Day, bool Enabled, string Start, string End);

public sealed record NotificationPreferenceDto(string Event, bool Email, bool Sms);

public sealed record DelegationDto(
    Guid Id,
    string PermissionCode,
    string PermissionLabel,
    string? ScopeType,
    string? ScopeLabel,
    string? AssignedByName,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate);
