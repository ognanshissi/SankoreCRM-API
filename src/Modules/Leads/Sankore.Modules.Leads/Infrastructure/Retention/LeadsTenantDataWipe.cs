namespace Sankore.Modules.Leads.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Infrastructure.WhatsAppMedia;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>
/// The leads schema of a TEST institution, emptied (data-purge lot 4): leads, projects, tasks,
/// activities, appointments, WhatsApp threads, imports… Its configuration stays (<see cref="Keep"/>).
/// </summary>
internal sealed class LeadsTenantDataWipe(
    LeadsDbContext db, IFileStore files, IServiceProvider services, ILogger<LeadsTenantDataWipe> logger) : ITenantDataWipe
{
    /// <summary>What a recette starts from again: how the institution sells, not what it sold.</summary>
    internal static readonly Type[] Keep =
    [
        typeof(LeadSetting), typeof(DispatchingRule), typeof(LeadSourceConfig), typeof(ScoringConfig),
        typeof(TaskTypeConfig), typeof(PipelineStageConfig), typeof(SlaConfig), typeof(TaskGenerationRule),
        typeof(QualificationTemplate), typeof(QualificationSection), typeof(ProjectTemplate),
        typeof(NurturingSequence), typeof(NurturingStep), typeof(WhatsAppTemplate), typeof(WhatsAppBotProfile),
        typeof(SdkVersion),
    ];

    public string Module => "Leads";

    public async Task<int> WipeAsync(Guid tenantId, CancellationToken ct)
    {
        // The import files live in the generic store, whose keys carry no tenant: read from the
        // rows BEFORE they go.
        var importFiles = await db.LeadImportJobs.IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.SourceType == LeadImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference)
            .ToListAsync(ct);

        var rows = await TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);

        await TechnicalFiles.DeleteAsync(files, importFiles, logger, ct);
        await TenantObjects.WipeAsync(services, tenantId, [TaskAttachmentStorage.Concern, WhatsAppMediaStorage.Concern], logger, ct);
        return rows;
    }
}
