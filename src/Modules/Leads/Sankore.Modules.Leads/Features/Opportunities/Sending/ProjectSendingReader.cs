namespace Sankore.Modules.Leads.Features.Opportunities.Sending;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.ProjectFields;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// What another module may read of a project to send its documents (project documents lot 1,
/// 2026-11-08, product owner: the documents are DEPOSITED in the project, then sent by e-mail or
/// WhatsApp). One perimeter for both reads — the project page's, <see cref="LeadVisibility"/> —
/// so a file the agent cannot open on the page cannot leave by mail either.
///
/// <para>
/// <b>The actor must be the signed-in caller</b>, as for <c>CanActorWorkLeadAsync</c>: the
/// visibility rule reads <see cref="ICurrentUser"/>, and evaluating it for somebody else while
/// answering for the parameter would vouch for a check never made.
/// </para>
/// </summary>
internal sealed class ProjectSendingReader(
    LeadsDbContext db,
    ICurrentUser currentUser,
    LeadVisibility visibility,
    ProjectTemplateResolver templates,
    IServiceProvider services)
{
    /// <summary>A send names a handful of files; more is not a send from a project page.</summary>
    public const int MaxFiles = 10;

    public async Task<ProjectForMail?> GetAsync(Guid tenantId, Guid projectId, Guid actorUserId, CancellationToken ct)
    {
        var project = await VisibleAsync(tenantId, projectId, actorUserId, ct);
        if (project is null) return null;

        var clientId = project.CustomerEntityType == "Customer" ? project.CustomerEntityId : null;
        if (clientId is null && project.LeadId is { } leadId)
        {
            clientId = await db.Leads.AsNoTracking()
                .Where(l => l.Id == leadId)
                .Select(l => l.ConvertedToCustomerId ?? l.CustomerId)
                .FirstOrDefaultAsync(ct);
        }

        var template = await templates.ForProjectAsync(project, ct);
        var closed = project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost;

        return new ProjectForMail(
            project.Id,
            project.LeadId,
            clientId,
            project.Title,
            project.Product,
            project.EstimatedAmount?.Amount,
            project.EstimatedAmount?.Currency,
            template.CurrentStep(project.StepId, project.Stage)?.Label,
            project.ExpectedCloseDate,
            closed);
    }

    public async Task<Result<IReadOnlyList<ProjectFileForSending>>> ReadFilesAsync(
        Guid tenantId, Guid projectId, IReadOnlyCollection<Guid> attachmentIds, Guid actorUserId, CancellationToken ct)
    {
        var project = await VisibleAsync(tenantId, projectId, actorUserId, ct);
        if (project is null) return Result.Fail<IReadOnlyList<ProjectFileForSending>>(OpportunityErrors.NotFound);

        var ids = attachmentIds.Distinct().ToList();
        if (ids.Count == 0) return Result.Ok<IReadOnlyList<ProjectFileForSending>>([]);
        if (ids.Count > MaxFiles) return Result.Fail<IReadOnlyList<ProjectFileForSending>>(ProjectAttachmentHandlers.TooMany);

        var rows = await db.ProjectAttachments.AsNoTracking()
            .Where(a => a.OpportunityId == project.Id && ids.Contains(a.Id) && a.DeletedAt == null)
            .ToListAsync(ct);
        if (rows.Count != ids.Count) return Result.Fail<IReadOnlyList<ProjectFileForSending>>(ProjectAttachmentHandlers.NotFound);

        // No key configured = no store registered: nothing was ever deposited, nothing can leave.
        if (services.GetService<ITaskAttachmentStore>() is not { } store)
            return Result.Fail<IReadOnlyList<ProjectFileForSending>>(ProjectAttachmentHandlers.Unavailable);

        var files = new List<ProjectFileForSending>(rows.Count);
        foreach (var id in ids)
        {
            var row = rows.First(r => r.Id == id);
            var bytes = await store.OpenAsync(row.TenantId, row.StorageRef, ct);
            if (bytes is null) return Result.Fail<IReadOnlyList<ProjectFileForSending>>(ProjectAttachmentHandlers.NotFound);
            files.Add(new ProjectFileForSending(row.Id, row.FieldKey, row.FileName, row.ContentType, bytes));
        }

        return Result.Ok<IReadOnlyList<ProjectFileForSending>>(files);
    }

    /// <summary>
    /// The project, when it is in the caller's perimeter. The ambient tenant filter applies — the
    /// caller is inside the agent's request — and the tenant given must be that one.
    /// </summary>
    private async Task<Opportunity?> VisibleAsync(Guid tenantId, Guid projectId, Guid actorUserId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || projectId == Guid.Empty || actorUserId == Guid.Empty) return null;
        if (actorUserId != currentUser.Id || tenantId != currentUser.TenantId) return null;

        return await visibility.Apply(db.Opportunities, db.Leads)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == projectId, ct);
    }
}
