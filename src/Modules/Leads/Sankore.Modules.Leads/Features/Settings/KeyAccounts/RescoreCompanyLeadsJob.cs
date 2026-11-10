namespace Sankore.Modules.Leads.Features.Settings.KeyAccounts;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Rescores a tenant's open company prospects after its key-account settings changed (key accounts
/// lot 2), so the lists and the dispatch read the new « Potentiel » at once rather than at each lead's
/// next rescore. Same shape as <see cref="RecalculateLeadScore.CoolDownLeadsJob"/>: batches saved on
/// their own, a <c>ScoreHistory</c> row <see cref="TriggerEvent"/> for a changed score, the status
/// never moves and no critical-change event is published — an administrator's setting is not news
/// about the prospect.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
[AutomaticRetry(Attempts = 0)]
public sealed class RescoreCompanyLeadsJob(IServiceScopeFactory scopes, ILogger<RescoreCompanyLeadsJob> logger)
{
    public const string TriggerEvent = "KEY_ACCOUNT_SETTINGS_CHANGED";

    private const int BatchSize = 500;

    private static readonly LeadStatus[] Closed =
        [LeadStatus.Converted, LeadStatus.Archived, LeadStatus.Lost, LeadStatus.Disqualified];

    public async Task<int> ExecuteAsync(Guid tenantId)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var rescored = await RescoreAsync(db, tenantId, CancellationToken.None);
        if (rescored > 0)
            logger.LogInformation("Tenant {TenantId}: {Rescored} company prospects rescored after the key-account settings changed.", tenantId, rescored);
        return rescored;
    }

    internal static async Task<int> RescoreAsync(LeadsDbContext db, Guid tenantId, CancellationToken ct)
    {
        var calculator = new LeadScoreCalculator(db);
        var rescored = 0;
        Guid? after = null;
        while (true)
        {
            var batch = await db.Leads.IgnoreQueryFilters().AsTracking()
                .Where(l => l.TenantId == tenantId
                         && l.ProspectType == LeadType.Corporate
                         && l.Score > 0
                         && !l.IsAnonymized
                         && !Closed.Contains(l.Status)
                         && (after == null || l.Id.CompareTo(after.Value) > 0))
                .OrderBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var lead in batch)
            {
                var (score, factorsJson) = await calculator.CalculateAsync(lead, ct);
                if (score != lead.Score)
                {
                    lead.RecordScoreUpdate(score);
                    db.ScoreHistories.Add(ScoreHistory.Create(lead.TenantId, lead.Id, score, TriggerEvent, factorsJson));
                    rescored++;
                }
                var level = await calculator.IntentLevelOfAsync(lead, score, ct);
                if (level != lead.IntentLevel) lead.UpdateIntentLevel(level);
            }

            await db.SaveChangesAsync(ct);
            after = batch[^1].Id;
            db.ChangeTracker.Clear();
        }
        return rescored;
    }
}
