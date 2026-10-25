namespace Sankore.Modules.Customers.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>
/// The customers schema of a TEST institution, emptied (data-purge lot 4): clients, holdings,
/// groups, timelines, imports, exports, erasure requests… Its configuration stays (<see cref="Keep"/>).
/// </summary>
internal sealed class CustomersTenantDataWipe(
    CustomersDbContext db, IFileStore files, ILogger<CustomersTenantDataWipe> logger) : ITenantDataWipe
{
    /// <summary>
    /// The client number sequence is kept with the settings: a recette that restarted at 1 would
    /// hand out numbers a previous recette's exports already used.
    /// </summary>
    internal static readonly Type[] Keep = [typeof(CustomerSetting), typeof(LegalForm), typeof(ClientNumberSequence)];

    public string Module => "Customers";

    public async Task<int> WipeAsync(Guid tenantId, CancellationToken ct)
    {
        // Generic store, keys without tenant: read from the rows before they go.
        var fileRefs = await db.ClientImportJobs.IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.SourceType == ClientImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference)
            .ToListAsync(ct);
        fileRefs.AddRange(await db.ClientExportJobs.IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.FileReference != null)
            .Select(j => j.FileReference!)
            .ToListAsync(ct));

        var rows = await TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);
        await TechnicalFiles.DeleteAsync(files, fileRefs, logger, ct);
        return rows;
    }
}
