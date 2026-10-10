namespace Sankore.Modules.Integration.Features.Mappings.ExportMappings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ExportMappingsHandler(IntegrationDbContext db)
    : IRequestHandler<ExportMappingsQuery, Result<MappingExportFile>>
{
    public async Task<Result<MappingExportFile>> Handle(
        ExportMappingsQuery query, CancellationToken ct)
    {
        if (!await db.ConnectionExistsAsync(query.ConnectionId, ct))
            return Result.Fail<MappingExportFile>(IntegrationErrors.ConnectionNotFound);

        var rows = await db.Mappings
            .Where(m => m.ConnectionId == query.ConnectionId && m.Domain == query.Domain)
            .OrderBy(m => m.CrmCode)
            .Select(m => new MappingExportRow(m.CrmCode, m.ExternalCode, m.Label ?? string.Empty))
            .ToListAsync(ct);

        // An empty domain exports the header alone rather than 404: that file is the template an
        // operator fills in and uploads back, which is how a table gets populated the first time.
        var content = await MappingExportCsv.WriteAsync(rows, ct);

        return Result.Ok(new MappingExportFile(
            content,
            MappingExportCsv.FileName(query.Domain, query.ConnectionId),
            MappingExportCsv.ContentType));
    }
}
