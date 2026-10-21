using System.Globalization;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

/// <summary>Ce que l'agent déclare de lui-même au dispatching.</summary>
public enum AgentAvailability
{
    /// <summary>Reçoit de nouveaux leads.</summary>
    Available,

    /// <summary>Ne reçoit plus de nouveaux leads mais garde et traite ses tâches.</summary>
    Paused,

    /// <summary>Congés, formation : aucun nouveau lead.</summary>
    Absent
}

/// <summary>Une journée de la semaine : travaillée ou non, et sa plage horaire (HH:mm).</summary>
public sealed record WorkingDay(DayOfWeek Day, bool Enabled, string Start, string End);

/// <summary>
/// Une absence déclarée : période (bornes incluses), remplaçant éventuel, et si les tâches en cours
/// lui sont transférées.
/// </summary>
public sealed record AbsenceDeclaration(DateOnly From, DateOnly To, Guid? ReplacementUserId, bool TransferTasks);

/// <summary>Un évènement et les canaux sur lesquels l'agent veut en être prévenu.</summary>
public sealed record NotificationPreference(string Event, bool Email, bool Sms);

/// <summary>Évènements que l'agent peut régler. Liste fermée : un code inconnu est refusé à l'écriture.</summary>
public static class AgentNotificationEvents
{
    public const string LeadAssigned = "LeadAssigned";
    public const string DeadlineApproaching = "DeadlineApproaching";
    public const string TaskReassignedOrEscalated = "TaskReassignedOrEscalated";
    public const string KycCompletedByClient = "KycCompletedByClient";

    public static readonly IReadOnlyList<string> All =
        [LeadAssigned, DeadlineApproaching, TaskReassignedOrEscalated, KycCompletedByClient];
}

/// <summary>
/// Paramétrage d'un agent pour le dispatching, réglé par l'agent lui-même — sauf le plafond de
/// capacité, fixé par son supérieur ou un administrateur. Une ligne par utilisateur, créée à la
/// première écriture : un agent sans ligne n'a déclaré aucune contrainte et reste dispatchable
/// comme avant.
///
/// Le statut est miroité sur <see cref="AppUser.IsAvailable"/>, que le dispatching filtre déjà :
/// c'est la même information, et la dupliquer sans la synchroniser donnerait deux réponses.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Plafond par défaut, aligné sur <c>DispatchingRule.Default().MaxLeadsPerAgent</c>.</summary>
    public const int DefaultCapacityCeiling = 30;
    public const int MaxCapacityCeiling = 500;

    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }

    public AgentAvailability Availability { get; private set; } = AgentAvailability.Available;

    /// <summary>
    /// Sept jours, ou aucun. Vide signifie « aucune contrainte horaire », pas « jamais disponible » :
    /// c'est l'état de tout agent qui n'a jamais ouvert l'écran.
    /// </summary>
    public List<WorkingDay> WorkingHours { get; private set; } = [];

    /// <summary>Nombre de leads actifs que l'agent accepte de suivre. Null : le plafond s'applique.</summary>
    public int? MaxActiveLeads { get; private set; }

    public int CapacityCeiling { get; private set; } = DefaultCapacityCeiling;

    public List<NotificationPreference> NotificationPreferences { get; private set; } = [];

    /// <summary>
    /// Whether the bell plays a sound when a notification arrives (WA-FE-10, « son désactivable
    /// dans les préférences »). One switch for the whole bell, not per event: it is about the
    /// person's surroundings — an open-plan agency — not about what is being notified.
    /// </summary>
    public bool NotificationSound { get; private set; } = true;

    // ── Absence (renseignée seulement quand Availability = Absent) ─────────
    public DateOnly? AbsenceFrom { get; private set; }
    public DateOnly? AbsenceTo { get; private set; }
    public Guid? ReplacementUserId { get; private set; }
    public bool TransferTasksToReplacement { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private AgentSettings() { }

    public static AgentSettings Create(Guid tenantId, Guid userId) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>La capacité retenue : celle de l'agent, jamais au-delà du plafond.</summary>
    public int EffectiveCapacity => Math.Min(MaxActiveLeads ?? CapacityCeiling, CapacityCeiling);

    /// <summary>
    /// Applique la déclaration de l'agent. Renvoie vrai quand l'absence est nouvelle ou a changé —
    /// c'est ce qui doit prévenir le superviseur et déclencher le transfert, pas chaque enregistrement.
    /// </summary>
    public void SetNotificationSound(bool enabled)
    {
        NotificationSound = enabled;
    }

    public bool UpdateBySelf(
        AgentAvailability availability,
        AbsenceDeclaration? absence,
        IReadOnlyCollection<WorkingDay> workingHours,
        int? maxActiveLeads,
        IReadOnlyCollection<NotificationPreference> notificationPreferences)
    {
        if (maxActiveLeads is < 1)
            throw new DomainException("La capacité doit être d'au moins un lead.", "AgentSettings.Capacity.TooLow");
        if (maxActiveLeads > CapacityCeiling)
            throw new DomainException(
                $"La capacité ne peut pas dépasser le plafond fixé par votre superviseur ({CapacityCeiling}).",
                "AgentSettings.Capacity.AboveCeiling");

        if (availability == AgentAvailability.Absent)
        {
            if (absence is null)
                throw new DomainException("Indiquez les dates de l'absence.", "AgentSettings.Absence.Required");
            if (absence.To < absence.From)
                throw new DomainException("La fin de l'absence doit suivre son début.", "AgentSettings.Absence.Order");
            if (absence.ReplacementUserId == UserId)
                throw new DomainException("Vous ne pouvez pas être votre propre remplaçant.", "AgentSettings.Absence.SelfReplacement");
            if (absence.TransferTasks && absence.ReplacementUserId is null)
                throw new DomainException("Choisissez un remplaçant pour lui transférer vos tâches.", "AgentSettings.Absence.ReplacementRequired");
        }

        var previousAbsence = CurrentAbsence;
        var nextAbsence = availability == AgentAvailability.Absent ? absence : null;

        Availability = availability;
        AbsenceFrom = nextAbsence?.From;
        AbsenceTo = nextAbsence?.To;
        ReplacementUserId = nextAbsence?.ReplacementUserId;
        TransferTasksToReplacement = nextAbsence?.TransferTasks ?? false;
        WorkingHours = ValidateWorkingHours(workingHours);
        MaxActiveLeads = maxActiveLeads;
        NotificationPreferences = ValidatePreferences(notificationPreferences);
        UpdatedAt = DateTimeOffset.UtcNow;

        return nextAbsence is not null && nextAbsence != previousAbsence;
    }

    public AbsenceDeclaration? CurrentAbsence =>
        Availability == AgentAvailability.Absent && AbsenceFrom is { } from && AbsenceTo is { } to
            ? new AbsenceDeclaration(from, to, ReplacementUserId, TransferTasksToReplacement)
            : null;

    /// <summary>
    /// Le statut seul autorise-t-il un nouveau lead ? Une absence n'écarte l'agent que pendant sa
    /// période : déclarée à l'avance, elle ne doit pas le couper du dispatching dès aujourd'hui, et
    /// passée, elle ne doit pas l'en tenir écarté jusqu'à ce qu'il pense à revenir sur cet écran.
    /// </summary>
    public bool AllowsDispatchOn(DateOnly day) => Availability switch
    {
        AgentAvailability.Available => true,
        AgentAvailability.Paused => false,
        _ => CurrentAbsence is { } a && (day < a.From || day > a.To),
    };

    /// <summary>
    /// Statut, horaires et capacité réunis : la seule question que pose le dispatching. Le jour et
    /// l'heure sont ceux du fuseau de l'agent (UTC à défaut) : une absence « jusqu'au 23 » finit
    /// à minuit chez lui, pas à Greenwich.
    /// </summary>
    public bool IsDispatchable(DateTimeOffset now, int activeLeads, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Utc);
        return AllowsDispatchOn(DateOnly.FromDateTime(local.DateTime))
               && IsWithinWorkingHours(now, timeZone)
               && HasCapacityFor(activeLeads);
    }

    /// <summary>
    /// Fixé par un supérieur. Une capacité déjà déclarée au-dessus est ramenée au plafond plutôt que
    /// refusée : l'agent n'a rien fait de mal, c'est la règle qui a changé.
    /// </summary>
    public void SetCapacityCeiling(int ceiling)
    {
        if (ceiling < 1 || ceiling > MaxCapacityCeiling)
            throw new DomainException(
                $"Le plafond doit être compris entre 1 et {MaxCapacityCeiling}.", "AgentSettings.Ceiling.OutOfRange");

        CapacityCeiling = ceiling;
        if (MaxActiveLeads > ceiling) MaxActiveLeads = ceiling;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// L'instant tombe-t-il dans une plage travaillée ? Sans horaires déclarés, toujours oui.
    /// Les heures sont lues dans le fuseau de l'agent, réglé sur son profil — UTC s'il n'en a pas.
    /// </summary>
    public bool IsWithinWorkingHours(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (WorkingHours.Count == 0) return true;

        var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Utc);
        var day = WorkingHours.FirstOrDefault(d => d.Day == local.DayOfWeek);
        if (day is null || !day.Enabled) return false;

        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return time >= ParseTime(day.Start) && time < ParseTime(day.End);
    }

    public bool HasCapacityFor(int activeLeads) => activeLeads < EffectiveCapacity;

    private static List<WorkingDay> ValidateWorkingHours(IReadOnlyCollection<WorkingDay> days)
    {
        if (days.Count == 0) return [];
        if (days.Count != 7 || days.Select(d => d.Day).Distinct().Count() != 7)
            throw new DomainException("Les horaires doivent couvrir les sept jours de la semaine.", "AgentSettings.Hours.Incomplete");

        foreach (var day in days.Where(d => d.Enabled))
        {
            if (!TryParseTime(day.Start, out var start) || !TryParseTime(day.End, out var end))
                throw new DomainException("Une heure doit être au format HH:mm.", "AgentSettings.Hours.Format");
            if (end <= start)
                throw new DomainException("L'heure de fin doit suivre l'heure de début.", "AgentSettings.Hours.Order");
        }

        return days.OrderBy(d => ((int)d.Day + 6) % 7).ToList();
    }

    private static List<NotificationPreference> ValidatePreferences(IReadOnlyCollection<NotificationPreference> preferences)
    {
        var unknown = preferences.FirstOrDefault(p => !AgentNotificationEvents.All.Contains(p.Event));
        if (unknown is not null)
            throw new DomainException($"Évènement de notification inconnu : {unknown.Event}.", "AgentSettings.Notification.UnknownEvent");

        return preferences.GroupBy(p => p.Event).Select(g => g.Last()).ToList();
    }

    private static bool TryParseTime(string value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static TimeOnly ParseTime(string value)
        => TryParseTime(value, out var time) ? time : TimeOnly.MinValue;
}
