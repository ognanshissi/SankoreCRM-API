namespace Sankore.Modules.Leads.Features.GetLead;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The first-contact SLA of a lead's current assignment, as the lead screens show it.
/// </summary>
/// <param name="Deadline">When the first contact is due.</param>
/// <param name="WarningAt">
/// From when the SLA counts as "about to be breached" — the instant <c>CheckSlaBreachesJob</c> warns
/// the agent, computed from the same tenant setting so the badge and the e-mail never disagree.
/// Null when the tenant disabled the warning.
/// </param>
/// <param name="FirstContactAt">When the first contact was recorded; null while it is still owed.</param>
public sealed record LeadSlaDto(
    DateTimeOffset Deadline,
    DateTimeOffset? WarningAt,
    DateTimeOffset? FirstContactAt);

/// <summary>
/// Resolves <see cref="LeadSlaDto"/> for a page of leads in one query. The lead itself only
/// carries <c>ExpiresAt</c> — its retention lifetime, 180 days by default — which the lead list
/// used to read as the SLA, so the « SLA en risque » badge lit up only days before a lead's
/// expiry and never for a late first contact.
/// </summary>
internal static class LeadSlas
{
    public static async Task<IReadOnlyDictionary<Guid, LeadSlaDto>> ForAssignmentsAsync(
        LeadsDbContext db,
        ILeadSettings? settings,
        Guid tenantId,
        IReadOnlyCollection<Guid> assignmentIds,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0) return new Dictionary<Guid, LeadSlaDto>();

        var assignments = await db.LeadAssignments
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && assignmentIds.Contains(a.Id))
            .ToListAsync(ct);

        var percent = settings is null
            ? int.Parse(LeadSettingKeys.DefaultsByKey[LeadSettingKeys.SlaWarningThresholdPercent].Value)
            : await settings.GetIntAsync(tenantId, LeadSettingKeys.SlaWarningThresholdPercent, ct);

        return assignments.ToDictionary(
            a => a.Id,
            a => new LeadSlaDto(a.SlaDeadline, a.SlaWarningAt(percent), a.FirstContactAt));
    }
}
