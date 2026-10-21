using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Immutable record of a single dispatching decision. A Lead can have many
/// LeadAssignment rows over its lifetime (initial dispatch, reassignment
/// after refusal, escalation after SLA breach) — this is the audit trail
/// F13.14 requires ("dispatching manuel supervisé... tracé").
/// </summary>
public sealed class LeadAssignment
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid AgentId { get; private set; }
    public DispatchingStrategy Strategy { get; private set; }

    /// <summary>
    /// The <see cref="DispatchingRule"/> that produced this assignment, or <c>null</c> when the
    /// built-in defaults were used. Without it nobody can tell, after the fact, which
    /// configuration routed a lead — nor measure the effect of changing a rule.
    /// </summary>
    public Guid? RuleId { get; private set; }
    public double CompatibilityScore { get; private set; }
    public bool WasManualOverride { get; private set; }
    public string? OverrideReason { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; } // When the assignment has done
    public DateTimeOffset SlaDeadline { get; private set; }
    public DateTimeOffset? FirstContactAt { get; private set; }

    /// <summary>
    /// When this assignment stopped being the lead's current one — a reassignment to another agent
    /// or a return to the dispatching queue. <c>null</c> for the assignment in force.
    ///
    /// <para>
    /// It exists because an assignment can only ever be closed through <see cref="RecordFirstContact"/>,
    /// which <c>RecordFirstContactHandler</c> only ever calls on <c>Lead.CurrentAssignmentId</c>. A
    /// replaced row therefore kept <see cref="FirstContactAt"/> null for ever, and
    /// <c>CheckSlaBreachesJob</c> — which scans the rows, not the lead's pointer — mailed an
    /// SLA-breach alert to the PREVIOUS agent every day the lead stayed open, while
    /// <c>GetSlaBreaches</c> and <c>GetAgentPerformance</c> counted the same row as still waiting
    /// for a first contact.
    /// </para>
    /// </summary>
    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>True once this row has been replaced: history, not an open piece of work.</summary>
    public bool IsSuperseded => SupersededAt is not null;

    /// <summary>
    /// JSON breakdown of per-factor contributions used to compute
    /// <see cref="CompatibilityScore"/> (language, product, geography,
    /// workload, performance, agency). Persisted for post-hoc audit of
    /// contested dispatching decisions (US-M13-072).
    /// </summary>
    public string CompatibilityFactorsJson { get; private set; } = "{}";

    private LeadAssignment() { } // EF Core

    public static LeadAssignment Create(
        Guid tenantId, Guid leadId, Guid agentId, DispatchingStrategy strategy,
        double compatibilityScore, DateTimeOffset slaDeadline, DateTimeOffset createdAt,
        string compatibilityFactorsJson = "{}", Guid? ruleId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LeadId = leadId,
            AgentId = agentId,
            Strategy = strategy,
            RuleId = ruleId,
            CompatibilityScore = compatibilityScore,
            WasManualOverride = false,
            CreatedAt = createdAt,
            SlaDeadline = slaDeadline,
            CompatibilityFactorsJson = compatibilityFactorsJson
        };

    public static LeadAssignment CreateManualOverride(
        Guid tenantId, Guid leadId, Guid agentId, string reason, DateTimeOffset slaDeadline, DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LeadId = leadId,
            AgentId = agentId,
            Strategy = DispatchingStrategy.CompatibilityScoring,
            CompatibilityScore = 0,
            WasManualOverride = true,
            OverrideReason = reason,
            CreatedAt = createdAt,
            SlaDeadline = slaDeadline
        };

    /// <summary>
    /// How <see cref="FirstContactAt"/> was established (TEL-B-12). Null for an assignment
    /// contacted before the source was tracked — see <see cref="FirstContactSource"/> for why that
    /// is not backfilled.
    /// </summary>
    public FirstContactSource? FirstContactSource { get; private set; }

    /// <summary>
    /// Records the first contact, once and for ever.
    ///
    /// <para>
    /// Still <c>??=</c> in effect, so the guarantee "never rewritten" is unchanged — a second
    /// call is a no-op. What changed with TEL-B-12 is that the instant and its SOURCE move
    /// TOGETHER: a source written on its own, or kept when the instant was refused, would say
    /// that a telephony call established a contact an agent had already declared.
    /// </para>
    ///
    /// <para>
    /// Answers whether it recorded anything, so a caller can tell "I stopped the clock" from
    /// "somebody already had" — which a consumer needs, because for it the second is a success
    /// and not a failure.
    /// </para>
    /// </summary>
    public bool RecordFirstContact(DateTimeOffset at, FirstContactSource source)
    {
        if (FirstContactAt is not null) return false;

        FirstContactAt = at;
        FirstContactSource = source;

        return true;
    }

    /// <summary>
    /// Closes this assignment as superseded. Idempotent, and it does NOT touch
    /// <see cref="FirstContactAt"/>: "the agent never called" and "the lead was taken off them"
    /// are different facts, and flattening the second into the first would silently improve every
    /// agent's contact rate.
    ///
    /// <para>
    /// Called by <c>Lead.AssignTo</c> and <c>Lead.ReturnToQueue</c>, which is the only reason the
    /// invariant holds — those are the only two methods that move
    /// <c>Lead.CurrentAssignmentId</c>, so a row cannot stop being current without passing here.
    /// </para>
    /// </summary>
    public void Supersede(DateTimeOffset at) => SupersededAt ??= at;

    /// <summary>
    /// A superseded assignment never breaches: nobody owes a first contact on a lead that has been
    /// taken off them, and the SLA of the work follows the assignment that replaced it.
    /// </summary>
    public bool HasBreachedSla(DateTimeOffset now)
        => !IsSuperseded && FirstContactAt is null && now > SlaDeadline;

    /// <summary>
    /// When the agent was warned that this assignment's SLA was about to be breached; null until
    /// then. A column rather than M08's idempotency alone: the sweep runs every 15 minutes, and
    /// without it every run would re-resolve and re-submit each warned assignment until it breached.
    /// </summary>
    public DateTimeOffset? SlaWarnedAt { get; private set; }

    /// <summary>
    /// The instant from which the SLA counts as "about to be breached": <paramref name="percent"/>
    /// of the window between the assignment and its deadline. Null when the warning is disabled
    /// (outside 1–99, see <c>LeadSettingKeys.SlaWarningThresholdPercent</c>).
    /// </summary>
    public DateTimeOffset? SlaWarningAt(int percent)
        => percent is < 1 or > 99
            ? null
            : CreatedAt + TimeSpan.FromTicks((SlaDeadline - CreatedAt).Ticks / 100 * percent);

    /// <summary>
    /// Open, not yet contacted, past the warning instant but not yet breached, and not warned
    /// already. Past the deadline the breach alert takes over.
    /// </summary>
    public bool ShouldWarnSla(DateTimeOffset now, int percent)
        => !IsSuperseded
        && FirstContactAt is null
        && SlaWarnedAt is null
        && now <= SlaDeadline
        && SlaWarningAt(percent) is { } warnAt
        && now >= warnAt;

    /// <summary>Records that the warning went out. Idempotent: the first instant is kept.</summary>
    public void MarkSlaWarned(DateTimeOffset at) => SlaWarnedAt ??= at;
}
