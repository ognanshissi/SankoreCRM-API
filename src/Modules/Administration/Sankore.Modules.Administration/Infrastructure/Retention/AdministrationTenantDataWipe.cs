namespace Sankore.Modules.Administration.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

/// <summary>
/// The administration schema is the institution's CONFIGURATION — users, roles, agencies, catalogue,
/// forms, company profile — and stays whole (data-purge lot 4). Only the user imports go, with
/// their files: they hold the personal data of the people imported, not the institution's setup.
/// Listed by hand, not by the generic wipe: here the keep-list would be the whole schema.
/// </summary>
internal sealed class AdministrationTenantDataWipe(
    AdministrationDbContext db, IFileStore files, ILogger<AdministrationTenantDataWipe> logger) : ITenantDataWipe
{
    public string Module => "Administration";

    public async Task<int> WipeAsync(Guid tenantId, CancellationToken ct)
    {
        var imports = db.UserImportJobs.IgnoreQueryFilters().Where(j => j.TenantId == tenantId);
        var fileRefs = await imports
            .Where(j => j.SourceType == UserImportSourceType.File && j.SourceReference != "")
            .Select(j => j.SourceReference)
            .ToListAsync(ct);

        var rows = await imports.ExecuteDeleteAsync(ct);
        await TechnicalFiles.DeleteAsync(files, fileRefs, logger, ct);
        return rows;
    }
}
