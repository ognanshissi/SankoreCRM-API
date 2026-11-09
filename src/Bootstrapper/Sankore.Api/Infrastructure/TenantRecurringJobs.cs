using System.Linq.Expressions;
using Hangfire;

namespace Sankore.Api.Infrastructure;

/// <summary>Where a recurring job is registered: the start-up's own registration honours paused jobs.</summary>
public interface IRecurringJobSink
{
    void AddOrUpdate<TJob>(string jobId, Expression<Func<TJob, Task>> methodCall, string cron);
}

/// <summary>
/// Registers through Hangfire, except the jobs an operator paused on the dashboard's « Job control »
/// page: re-registering one would resurrect it.
/// </summary>
public sealed class PauseAwareJobSink(IRecurringJobManager manager, IReadOnlyCollection<string> paused) : IRecurringJobSink
{
    public List<string> Skipped { get; } = [];

    public void AddOrUpdate<TJob>(string jobId, Expression<Func<TJob, Task>> methodCall, string cron)
    {
        if (paused.Contains(jobId))
        {
            Skipped.Add(jobId);
            return;
        }

        manager.AddOrUpdate(jobId, methodCall, cron);
    }
}

/// <summary>
/// The recurring jobs every tenant has, one set per tenant. Moved out of Program.cs (2026-11-08) so a
/// tenant created while the API runs gets them when its starting data is loaded, instead of at the
/// next restart.
/// </summary>
public static class TenantRecurringJobs
{
    public static void Register(Guid tenantId, IRecurringJobSink jobs)
    {
        var suffix = tenantId.ToString()[..8];


        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.SlaMonitoring.CheckSlaBreachesJob>(
            $"sla-breach-check-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/15 * * * *");

        // WA-BE-04 : le catalogue de templates WhatsApp, toutes les heures. Sans numéro actif, le
        // job ne fait rien.
        // WA-BE-15 : SLA de prise en charge et fenêtre de 24 h, toutes les deux minutes.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.WhatsApp.WhatsAppTakeoverJob>(
            $"{Sankore.Modules.Leads.Features.WhatsApp.WhatsAppTakeoverJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.WhatsApp.WhatsAppTakeoverJob.CronExpression);

        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.WhatsApp.SyncWhatsAppTemplatesJob>(
            $"{Sankore.Modules.Leads.Features.WhatsApp.SyncWhatsAppTemplatesJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.WhatsApp.SyncWhatsAppTemplatesJob.CronExpression);

        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.NurturingExecution.ExecuteNurturingJob>(
            $"nurturing-execution-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/10 * * * *");

        // Projets à date de clôture critique (crédit de campagne…) : une tâche urgente par date
        // dépassée, chaque matin.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.ProjectTemplates.ProjectCloseDateOverdueJob>(
            $"{Sankore.Modules.Leads.Features.ProjectTemplates.ProjectCloseDateOverdueJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.ProjectTemplates.ProjectCloseDateOverdueJob.CronExpression);

        // La cloche pour ce qui arrive à échéance : tâche en retard, rendez-vous proche, rappel dû.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.Tasks.WorkAlerts.WorkAlertsJob>(
            $"{Sankore.Modules.Leads.Features.Tasks.WorkAlerts.WorkAlertsJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.Tasks.WorkAlerts.WorkAlertsJob.CronExpression);

        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.ReactivateRecycledLeads.ReactivateRecycledLeadsJob>(
            $"reactivate-recycled-leads-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "0 3 * * *");

        // Lead temperature, lot 4: untouched open leads are rescored so a forgotten hot lead cools.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.RecalculateLeadScore.CoolDownLeadsJob>(
            $"{Sankore.Modules.Leads.Features.RecalculateLeadScore.CoolDownLeadsJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.RecalculateLeadScore.CoolDownLeadsJob.CronExpression);

        // Dispatch queue: leads no agent could take are retried under the dispatching rule until
        // one becomes available. Every two minutes — the wait an agent coming back from a break
        // sees before the queue reaches them — and nearly free when nobody is available.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.DispatchQueue.DispatchWaitingLeadsJob>(
            $"dispatch-waiting-leads-{suffix}",
            job => job.ExecuteAsync(tenantId),
            "*/2 * * * *");

        // Health questionnaire retention (BNF-B-09). Nightly, offset from the other jobs: it
        // reads the tenant's own retention period and blanks only answers already transmitted to
        // the insurer, so there is nothing to race with.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.Questionnaires.PurgeHealthResponsesJob>(
            $"purge-health-responses-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 3 * * *");

        // Lead anonymisation (BNF-B-19). AFTER the health purge, deliberately: that one blanks
        // transmitted answers on leads of any age, this one destroys everything about leads closed
        // for the tenant's retention period. Running them the other way round would have the
        // anonymisation purge answers the health job is the right place to reason about.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.Retention.AnonymizeClosedLeadsJob>(
            $"anonymize-closed-leads-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "45 3 * * *");

        // WA-BE-21: AFTER the lead anonymisation, so the media of the conversations it just
        // anonymised are purged the same night.
        jobs.AddOrUpdate<Sankore.Modules.Leads.Features.WhatsApp.WhatsAppRetentionJob>(
            $"{Sankore.Modules.Leads.Features.WhatsApp.WhatsAppRetentionJob.RecurringJobPrefix}-{suffix}",
            job => job.ExecuteAsync(tenantId),
            Sankore.Modules.Leads.Features.WhatsApp.WhatsAppRetentionJob.CronExpression);

        // Contract-anniversary KYC updates (CIMA LBC/FT: « les informations sont mises à jour à la
        // date anniversaire du contrat »). A different rule from the periodic review above, with a
        // different clock: that one follows the vigilance level, this one the contract's date.
        //
        // 02:15, AFTER the 01:00 periodic review sweep, deliberately. A file that was already due
        // for a periodic review this morning has an open review by then, and the anniversary job
        // skips a file that has one — so a client does not get two tasks in one night for the two
        // rules landing on the same day. The other order would raise the anniversary review first
        // and let the periodic sweep add its own beside it.
        jobs.AddOrUpdate<Sankore.Modules.Kyc.Features.Reviews.ContractAnniversaryReviewJob>(
            $"kyc-contract-anniversary-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "15 2 * * *");

        // ASS-07: the insurers' own view of their policies, into the read model the
        // reconciliation run and the monthly statement both read. At 03:30, BEFORE INT-34's
        // reconciliation at 06:00 — deliberately: reconciling against a read model a day stale
        // would report every overnight change at the insurer as a gap to arbitrate.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.PolicySync.SyncPoliciesJob>(
            $"insurance-policy-sync-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 3 * * *");

        // ASS-08: the premium schedule, then the debits that have fallen due. AFTER the policy
        // sync of the same night, deliberately — a policy discovered at 03:30 gets its schedule
        // at 03:45 rather than waiting a day, and a periodicity corrected at the insurer is
        // honoured by the same run that learned about it.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Premiums.CollectPremiumsJob>(
            $"insurance-premium-collection-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "45 3 * * *");

        // ASS-09: the open dossiers at each insurer. At 04:00, after the policy sync — a claim
        // discovered on a policy the same night is then placed on a contract this platform
        // already knows, instead of being dropped for want of one.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Claims.ClaimSync.SyncClaimsJob>(
            $"insurance-claim-sync-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 4 * * *");

        // ASS-10: the previous month's bordereau per insurer. MONTHLY, on the 1st at 05:00 —
        // after that night's policy sync and premium collection, so the last day of the month is
        // in the figures, and before INT-34's 06:00 reconciliation. It rebuilds its own draft, so
        // running it again on the 2nd is harmless; a month already finalised is refused.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Statements.GenerateStatementsJob>(
            $"insurance-statement-generation-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 5 1 * *");

        // ASS-12: dates the insurance evidence, then destroys what has outlived its retention.
        // WEEKLY and not nightly, on purpose — a ten-year deadline does not need a daily sweep,
        // and the one job in the module whose mistake cannot be undone by running it again should
        // run as seldom as its job allows. Its destructive half is off unless
        // Integration:Insurance:Retention:PurgeEnabled says otherwise.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Retention.PurgeInsuranceEvidenceJob>(
            $"insurance-evidence-retention-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 2 * * 0");

        // The general client complaints register (2026-10-26): the late acknowledgements and answers,
        // reported once each to the agency manager. Same working-hours slot as the CIMA register's.
        jobs.AddOrUpdate<Sankore.Modules.Customers.Features.Complaints.ClientComplaintDeadlineJob>(
            $"client-complaint-deadlines-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 7 * * *");

        // The règlement 01/CIMA/2024's complaints mechanism, chased before it is breached. Daily
        // at 07:00, deliberately in working hours and not with the nightly sweeps: it produces a
        // task for an agent who has until the end of the day to act on it.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Complaints.ComplaintDeadlineJob>(
            $"insurance-complaint-deadlines-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "0 7 * * *");

        // Insurance lot 2: the distributor's pieces (agrément, garantie financière, RC pro) at 60,
        // 30 and 7 days from expiry and on the day — once expired, new subscriptions are refused.
        jobs.AddOrUpdate<Sankore.Modules.Administration.Features.InsuranceDistribution.DistributorDocumentExpiryJob>(
            $"insurance-distributor-documents-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "10 7 * * *");

        // Insurance lot 2: premiums still owed on a contract sent without them (insurer collects,
        // or the 60-day derogation) — chased before the deadline and on it.
        // Insurance lots 3-4: the insurer's 15 days, renunciation refunds, 30-day remittances.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.AfterSale.InsuranceFollowUpJob>(
            $"insurance-follow-up-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "30 7 * * *");

        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.PolicySync.PolicyExpiryAlertsJob>(
            $"insurance-policy-expiry-alerts-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "25 7 * * *");

        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Premiums.PremiumPaymentDeadlineJob>(
            $"insurance-premium-deadlines-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "20 7 * * *");

        // ASS-09's antivirus pass, then the transmission of the pieces the insurer asked for.
        // EVERY TEN MINUTES and not nightly: an agent at a counter deposits a complement and the
        // insurer is waiting on it, so a dossier must not sit a day on a scan that takes a second.
        // Both passes are bounded per run, and a piece left unscanned by one sweep is picked up by
        // the next.
        jobs.AddOrUpdate<Sankore.Modules.Integration.Features.Insurance.Claims.Documents.ScanClaimDocumentsJob>(
            $"insurance-claim-document-scan-{suffix}",
            job => job.RunAsync(tenantId, CancellationToken.None),
            "*/10 * * * *");
    }
}
