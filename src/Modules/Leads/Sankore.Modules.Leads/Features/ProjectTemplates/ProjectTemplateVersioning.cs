namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Writes a template's versions when it is saved — see <see cref="ProjectTemplateVersion"/>. Both
/// calls add to the context and leave the save to the handler, so the template and its version
/// commit together.
/// </summary>
internal static class ProjectTemplateVersioning
{
    /// <summary>
    /// Before the first edit of a template that has no version yet: freezes what it says now as
    /// version 1, the one the projects created before versions keep following.
    /// </summary>
    /// <remarks>
    /// With <paramref name="templates"/>, the projects that follow it without a version are attached
    /// to that version 1 at the same time. They would read it anyway; attached, a template created
    /// later for their product no longer pulls them over.
    /// </remarks>
    public static async Task KeepBaselineAsync(
        LeadsDbContext db, ProjectTemplate template, DateTimeOffset now, CancellationToken ct,
        ProjectTemplateResolver? templates = null)
    {
        if (await db.ProjectTemplateVersions.AnyAsync(v => v.TemplateId == template.Id, ct))
            return;

        var baseline = ProjectTemplateVersion.Create(template, 1, now, baseline: true);
        db.ProjectTemplateVersions.Add(baseline);

        if (templates is null)
            return;

        var unattached = await db.Opportunities.AsTracking().Where(o => o.TemplateVersionId == null).ToListAsync(ct);
        foreach (var byProduct in unattached.GroupBy(o => o.Product))
        {
            if ((await templates.ResolveAsync(byProduct.Key, ct)).Id != template.Id)
                continue;
            foreach (var project in byProduct)
                project.FollowTemplateVersion(baseline.Id, now);
        }
    }

    /// <summary>
    /// After a save: the template's content becomes its latest version — a new number when the
    /// latest is in use, the same one rewritten when no project follows it yet, nothing when the
    /// save changed nothing a project reads (a rename alone still counts: the name is shown).
    /// </summary>
    public static async Task RecordAsync(
        LeadsDbContext db, ProjectTemplate template, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = ProjectTemplateSnapshot.Of(template).ToJson();

        var latest = db.ProjectTemplateVersions.Local
                         .Where(v => v.TemplateId == template.Id)
                         .MaxBy(v => v.Number)
                     ?? await db.ProjectTemplateVersions
                         .AsTracking()
                         .Where(v => v.TemplateId == template.Id)
                         .OrderByDescending(v => v.Number)
                         .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            db.ProjectTemplateVersions.Add(ProjectTemplateVersion.Create(template, 1, now));
            return;
        }

        // Compared through the serializer, not as stored: PostgreSQL rewrites a jsonb document
        // (key order, spacing), so the stored text never equals a fresh serialization.
        if (latest.Read().ToJson() == snapshot)
            return;

        var inUse = latest.IsBaseline
                    || await db.Opportunities.AnyAsync(o => o.TemplateVersionId == latest.Id, ct);

        if (inUse)
            db.ProjectTemplateVersions.Add(ProjectTemplateVersion.Create(template, latest.Number + 1, now));
        else
            latest.Rewrite(snapshot, now);
    }
}
