namespace Sankore.Modules.Leads.Features.Settings.PreviewRetention;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class PreviewRetentionHandler(
    LeadsDbContext db,
    ILeadSettings settings,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<PreviewRetentionQuery, Result<RetentionPreviewDto>>
{
    internal const string Warning =
        "L'anonymisation est IRRÉVERSIBLE : les noms, téléphones, e-mails, index aveugles, "
        + "classification PPE et déclarations FATCA des leads concernés sont détruits. "
        + "L'historique de tunnel (source, campagne, score, statut, dates) est conservé.";

    public async Task<Result<RetentionPreviewDto>> Handle(
        PreviewRetentionQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        var months = await settings.GetIntAsync(
            tenantId, LeadSettingKeys.LeadRetentionMonths, ct);

        // Zero or negative SKIPS the run, so the preview says that rather than answering a count.
        // Zero would read as "nothing to destroy", which is the opposite of what a typo means.
        if (months <= 0)
            return Result.Fail<RetentionPreviewDto>(LeadSettingErrors.RetentionNotConfigured);

        var cutoff = RetentionSelection.CutoffFor(clock.GetUtcNow(), months);

        // The SAME predicate the job runs, by construction — see RetentionSelection. The tenant
        // query filter would suffice here, but the expression carries its own tenant predicate
        // because the job has to pass one; applying it twice is harmless and keeps one expression
        // for both callers.
        var due = db.Leads.Where(RetentionSelection.Due(tenantId, cutoff));

        var dueNow = await due.CountAsync(ct);

        var oldest = dueNow == 0
            ? (DateTimeOffset?)null
            : await due.MinAsync(l => l.UpdatedAt, ct);

        return Result.Ok(new RetentionPreviewDto(
            RetentionMonths: months,
            Cutoff: cutoff,
            DueNow: dueNow,
            MaxPerRun: AnonymizeClosedLeadsJob.MaxLeadsPerRun,
            OldestClosedAt: oldest,
            IrreversibleWarning: Warning));
    }
}
