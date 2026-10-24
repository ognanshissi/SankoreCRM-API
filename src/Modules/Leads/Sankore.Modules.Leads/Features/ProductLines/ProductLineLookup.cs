namespace Sankore.Modules.Leads.Features.ProductLines;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The catalogue line a product label names — the one rule of M12's
/// <c>ResolveProductLineAsync</c> (exact on the code or the name, active lines, « Unknown » names
/// nothing), behind a suppressed transaction scope and best-effort.
///
/// <para>
/// <b>Why it exists</b> (product owner, 2026-10-24): the lead stores no product line any more —
/// the sale's line is the PROJECT's. What still needs a line before any project exists (the
/// licensed agents of a dispatch, a WhatsApp profile or template, the household ceilings, a report
/// by product) resolves the lead's <see cref="Lead.InterestedProduct"/> here, on demand. M12 caches
/// the catalogue (<c>ProductCatalogCache</c>), so a resolution costs a cached read.
/// </para>
///
/// <para>
/// <b>Best-effort</b>: an outage answers null, which every pre-project caller already reads as « no
/// line ». A GATE must not — the project's step gate and its questionnaire plan keep their own
/// fail-closed reads.
/// </para>
/// </summary>
internal static class ProductLineLookup
{
    /// <summary>The line <paramref name="label"/> names, or null (blank, unknown, outage).</summary>
    public static async Task<Guid?> ResolveAsync(
        IAdministrationModule administration, Guid tenantId, string? label, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        try
        {
            // Suppressed: M12 reads through its own DbContext, and a second connection enlisting in
            // a command's ambient scope promotes it to a distributed transaction Npgsql refuses.
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var lineId = await administration.ResolveProductLineAsync(tenantId, label.Trim(), ct);
            suppressed.Complete();
            return lineId is { } id && id != Guid.Empty ? id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>The line the lead ASKED for — what pre-project readers use.</summary>
    public static Task<Guid?> ForLeadAsync(IAdministrationModule administration, Lead lead, CancellationToken ct)
        => ResolveAsync(administration, lead.TenantId, lead.InterestedProduct, ct);

    /// <summary>
    /// The line a client file is opened for: the named project's, else the lead's single open
    /// project's, else what the lead asked for. ONE rule for the opening and for the completeness
    /// report shown before it, so the gauge never says « complet » about another product's policy.
    /// A named project that is not one of this lead's open projects is ignored rather than trusted.
    /// </summary>
    public static async Task<Guid?> ForClientFileAsync(
        LeadsDbContext db, IAdministrationModule administration, Lead lead, Guid? projectId, CancellationToken ct)
    {
        var open = await db.Opportunities
            .Where(o => o.LeadId == lead.Id
                     && o.Stage != OpportunityStage.ClosedWon
                     && o.Stage != OpportunityStage.ClosedLost)
            .ToListAsync(ct);

        var project = projectId is { } id
            ? open.FirstOrDefault(o => o.Id == id)
            : open.Count == 1 ? open[0] : null;

        return project is not null
            ? await ForProjectAsync(administration, project, ct)
            : await ForLeadAsync(administration, lead, ct);
    }

    /// <summary>
    /// The project's line: the one stored when its product was set, else resolved now — a project
    /// created before the column existed, or whose product was not in the catalogue then.
    /// </summary>
    public static async Task<Guid?> ForProjectAsync(
        IAdministrationModule administration, Opportunity project, CancellationToken ct)
        => project.ProductLineId ?? await ResolveAsync(administration, project.TenantId, project.Product, ct);
}
