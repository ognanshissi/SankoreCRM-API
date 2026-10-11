namespace Sankore.Modules.Leads.Features.Retention;

using System.Linq.Expressions;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// The ONE definition of "which leads the anonymisation run will destroy" (BNF-B-19).
///
/// <para>
/// It exists because the preview and the job must select the same rows. A preview that
/// re-implements the predicate is worse than no preview: it is read as a safety check before an
/// IRREVERSIBLE operation, and the day the two drift the number an administrator approved is not
/// the number of leads destroyed. Same reasoning as <c>LeadCompletenessCalculator</c> — a screen
/// with its own arithmetic eventually disagrees with the gate it is explaining.
/// </para>
///
/// <para>
/// The terminal statuses are LISTED rather than expressed as "not live", deliberately: a status
/// added later must not start being anonymised because nobody remembered this expression exists.
/// </para>
/// </summary>
internal static class RetentionSelection
{
    /// <summary>
    /// Leads of <paramref name="tenantId"/> closed before <paramref name="cutoff"/> and not yet
    /// anonymised. Combined with <c>IgnoreQueryFilters()</c> by both callers, which is why the
    /// tenant predicate is part of the expression rather than left to the ambient filter: a
    /// Hangfire job runs outside any HTTP context.
    /// </summary>
    internal static Expression<Func<Lead, bool>> Due(Guid tenantId, DateTimeOffset cutoff)
        => l => l.TenantId == tenantId
             && !l.IsAnonymized
             && (l.Status == LeadStatus.Converted
              || l.Status == LeadStatus.Lost
              || l.Status == LeadStatus.Disqualified
              || l.Status == LeadStatus.Archived)
             && l.UpdatedAt < cutoff;

    /// <summary>
    /// The cutoff a run started now would use. Negative or zero months never reach here — the job
    /// treats that as "not configured" and skips, because it is what a settings typo produces and
    /// the mistake cannot be undone.
    /// </summary>
    internal static DateTimeOffset CutoffFor(DateTimeOffset now, int retentionMonths)
        => now.AddMonths(-retentionMonths);
}
