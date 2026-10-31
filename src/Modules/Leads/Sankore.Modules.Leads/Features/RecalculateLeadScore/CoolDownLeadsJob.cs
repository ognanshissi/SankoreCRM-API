namespace Sankore.Modules.Leads.Features.RecalculateLeadScore;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// The nightly cooling (lead temperature, lot 4), per tenant: every open lead untouched for a week
/// is rescored, so the recency of its interactions and the inactivity penalty
/// (<see cref="LeadScoreCalculator.InactivityPenalty"/>) bring a forgotten hot lead down — a score
/// only moved when something happened to the lead, so one nobody touched never cooled.
///
/// <para>
/// A rescore, not a qualification: the status does not move. A changed score writes a
/// <c>ScoreHistory</c> row <see cref="TriggerEvent"/>; a lead whose score did not change but whose
/// temperature now reads differently (thresholds changed) only gets its temperature. No
/// <c>LeadScoreCriticallyChangedIntegrationEvent</c>: a slow drift crossing 60 overnight is not the
/// news that event carries, and a night of them would be a flood. In batches, each saved on its own,
/// so a run cut short keeps what it did and the next night resumes.
/// </para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
[AutomaticRetry(Attempts = 0)]
public sealed class CoolDownLeadsJob(IServiceScopeFactory scopes, ILogger<CoolDownLeadsJob> logger)
{
    public const string RecurringJobPrefix = "cool-down-leads";

    /// <summary>04:15, after the night's purges and reactivations.</summary>
    public const string CronExpression = "15 4 * * *";

    public const string TriggerEvent = "NIGHTLY_COOLDOWN";

    /// <summary>Before a week without a touch, nothing the calculator reads has aged.</summary>
    internal static readonly TimeSpan Untouched = TimeSpan.FromDays(7);

    private const int BatchSize = 500;

    private static readonly LeadStatus[] Closed =
        [LeadStatus.Converted, LeadStatus.Archived, LeadStatus.Lost, LeadStatus.Disqualified];

    public async Task<int> ExecuteAsync(Guid tenantId)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var calculator = new LeadScoreCalculator(db);
        var cutoff = DateTimeOffset.UtcNow - Untouched;

        var (rescored, cooled) = (0, 0);
        Guid? after = null;
        while (true)
        {
            var batch = await db.Leads.IgnoreQueryFilters().AsTracking()
                .Where(l => l.TenantId == tenantId
                         && l.Score > 0
                         && !l.IsAnonymized
                         && !Closed.Contains(l.Status)
                         && (l.LastActivityAt ?? l.CapturedAt) <= cutoff
                         && (after == null || l.Id.CompareTo(after.Value) > 0))
                .OrderBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync();
            if (batch.Count == 0) break;

            foreach (var lead in batch)
            {
                var (score, factorsJson) = await calculator.CalculateAsync(lead, CancellationToken.None);
                var level = await calculator.IntentLevelOfAsync(lead, score, CancellationToken.None);
                if (score != lead.Score)
                {
                    if (score < lead.Score) cooled++;
                    lead.RecordScoreUpdate(score);
                    db.ScoreHistories.Add(ScoreHistory.Create(lead.TenantId, lead.Id, score, TriggerEvent, factorsJson));
                    rescored++;
                }
                if (level != lead.IntentLevel) lead.UpdateIntentLevel(level);
            }

            await db.SaveChangesAsync();
            after = batch[^1].Id;
            db.ChangeTracker.Clear();
        }

        if (rescored > 0)
            logger.LogInformation("Tenant {TenantId}: {Rescored} untouched leads rescored overnight, {Cooled} cooled.", tenantId, rescored, cooled);
        return rescored;
    }
}
