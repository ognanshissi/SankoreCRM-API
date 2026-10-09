namespace Sankore.Modules.Integration.Features.Mappings.ImportMappings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ImportMappingsHandler(
    IntegrationDbContext db,
    MappingImportReader reader,
    MappingImportValidator validator,
    IFileStore fileStore,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<ImportMappingsCommand, Result<MappingImportReport>>
{
    public async Task<Result<MappingImportReport>> Handle(
        ImportMappingsCommand cmd, CancellationToken ct)
    {
        try
        {
            if (!await db.ConnectionExistsAsync(cmd.ConnectionId, ct))
                return Result.Fail<MappingImportReport>(IntegrationErrors.ConnectionNotFound);

            var file = await reader.ReadAsync(cmd.FileReference, ct);

            // A missing header is not a line problem: there is no line to report against, and
            // reporting every line as invalid would hide the one thing to fix.
            if (file.HasHeaderError)
                return Result.Fail<MappingImportReport>(file.HeaderError!);

            var report = validator.Validate(file.Rows);

            var rowsByNumber = file.Rows.ToDictionary(r => r.RowNumber);

            var validRows = report.Rows
                .Where(r => r.IsValid)
                .Select(r => rowsByNumber[r.RowNumber])
                .ToList();

            if (validRows.Count > 0)
                await ApplyAsync(cmd, validRows, ct);

            // An invalid line creates NOTHING — and the valid lines around it still land. The
            // alternative, refusing the whole file, is what makes an operator fix four hundred
            // lines to re-upload because of one.
            return Result.Ok(report);
        }
        finally
        {
            // The upload is consumed, not archived: the mapping rows ARE the record of what was
            // imported, and the audit row says who did it. CancellationToken.None because a
            // cancelled request must still not leak the file.
            if (cmd.DeleteAfterwards)
                await fileStore.DeleteAsync(cmd.FileReference, CancellationToken.None);
        }
    }

    private async Task ApplyAsync(
        ImportMappingsCommand cmd, List<MappingImportRow> rows, CancellationToken ct)
    {
        // A List and not an array: in .NET 10 `array.Contains(x)` inside an EF predicate binds to
        // the ReadOnlySpan<T> extension and no longer translates to SQL.
        var codes = rows.Select(r => r.CrmCode!.Trim()).ToList();

        // One query for the whole file rather than one per line: the import is synchronous and an
        // operator is watching it.
        var existing = await db.Mappings
            .AsTracking()
            .Where(m => m.ConnectionId == cmd.ConnectionId
                        && m.Domain == cmd.Domain
                        && codes.Contains(m.CrmCode))
            .ToDictionaryAsync(m => m.CrmCode, StringComparer.Ordinal, ct);

        foreach (var row in rows)
        {
            var crmCode = row.CrmCode!.Trim();
            var externalCode = row.ExternalCode!.Trim();
            var label = string.IsNullOrWhiteSpace(row.Label) ? null : row.Label.Trim();

            // An import of an existing code is an update, exactly like the PUT: re-uploading a
            // corrected file is the normal way this table is maintained.
            if (existing.TryGetValue(crmCode, out var mapping))
            {
                mapping.Update(externalCode, label, currentUser.Id, clock);
                continue;
            }

            db.Mappings.Add(IntegrationMapping.Create(
                tenant.CurrentTenantId,
                cmd.ConnectionId,
                cmd.Domain,
                crmCode,
                externalCode,
                currentUser.Id,
                clock,
                label));
        }

        await db.SaveChangesAsync(ct);
    }
}
