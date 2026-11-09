namespace Sankore.Modules.Leads.Features.Opportunities.Sending;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// What a project keeps of a document sent (project documents lot 4, 2026-11-08): a « Document
/// envoyé » line in its history — the channel, the files as they were named then — and each file
/// marked handed over, which the client's « Documents » tab reads as « Remis ».
///
/// <para>
/// <b>Written when the provider ACCEPTED</b> — the e-mail left, the WhatsApp message was taken —
/// never when it was merely queued: the history says what happened. The caller saves, in the save
/// that records the sending. Reads past the tenant filter with an explicit tenant: the WhatsApp
/// consumer and the mailbox's scheduled job act for nobody signed in.
/// </para>
/// </summary>
internal sealed class ProjectDocumentTrail(LeadsDbContext db, TimeProvider clock)
{
    public async Task RecordAsync(
        Guid tenantId, Guid projectId, string channel, IReadOnlyCollection<Guid> attachmentIds, Guid? actorId,
        Guid? relatedId, CancellationToken ct)
    {
        var ids = attachmentIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var project = await db.Opportunities.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == projectId, ct);
        if (project is null) return;

        // A file removed since is still named: it was sent, and the line says what left.
        var files = await db.ProjectAttachments.IgnoreQueryFilters().AsTracking()
            .Where(a => a.TenantId == tenantId && a.OpportunityId == projectId && ids.Contains(a.Id))
            .ToListAsync(ct);
        if (files.Count == 0) return;

        var now = clock.GetUtcNow();
        foreach (var file in files) file.MarkSent(channel, now);

        var names = string.Join(" ; ", ids.Select(id => files.FirstOrDefault(f => f.Id == id)?.FileName).Where(n => n is not null));
        if (names.Length > ProjectHistoryEntry.DetailMaxLength) names = names[..(ProjectHistoryEntry.DetailMaxLength - 1)] + "…";

        db.ProjectHistory.Add(ProjectHistoryEntry.Record(
            project, ProjectHistoryKind.DocumentSent, actorId, now, to: channel, detail: names, relatedId: relatedId));
    }
}
