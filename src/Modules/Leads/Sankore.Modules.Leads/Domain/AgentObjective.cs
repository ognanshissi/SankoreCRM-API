namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// What a manager expects of one agent from a month on (« Ma journée » redesign, lot 1, product
/// owner 2026-10-31): the share of first contacts made within 24 hours of the assignment, and the
/// leads converted in the month. One row per (agent, month); a month with no row reads the latest
/// earlier one (<see cref="Month"/> ≤ the month asked), so an objective is set once and carried
/// until changed. A null target is « no objective » from that month on — a row with both null
/// stops an earlier objective.
/// </summary>
public sealed class AgentObjective : ITenant
{
    public const int MaxConversions = 1000;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AgentId { get; private set; }

    /// <summary>The first day of the month the targets start applying.</summary>
    public DateOnly Month { get; private set; }

    /// <summary>0–100: first contacts made within 24 h of the assignment, in percent.</summary>
    public int? FirstContactWithin24hPercent { get; private set; }

    /// <summary>Leads converted to clients in the month.</summary>
    public int? Conversions { get; private set; }

    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private AgentObjective() { }

    public static AgentObjective Create(
        Guid tenantId, Guid agentId, DateOnly month, int? firstContactWithin24hPercent, int? conversions,
        Guid actor, DateTimeOffset now)
    {
        var objective = new AgentObjective
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgentId = agentId,
            Month = FirstOfMonth(month),
        };
        objective.Set(firstContactWithin24hPercent, conversions, actor, now);
        return objective;
    }

    public void Set(int? firstContactWithin24hPercent, int? conversions, Guid actor, DateTimeOffset now)
    {
        if (firstContactWithin24hPercent is < 0 or > 100)
            throw new DomainException("The first-contact target is a percentage.", "AgentObjective.FirstContact.Range");
        if (conversions is < 0 or > MaxConversions)
            throw new DomainException("The conversions target is out of range.", "AgentObjective.Conversions.Range");

        FirstContactWithin24hPercent = firstContactWithin24hPercent;
        Conversions = conversions;
        UpdatedBy = actor;
        UpdatedAt = now;
    }

    public static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);
}
