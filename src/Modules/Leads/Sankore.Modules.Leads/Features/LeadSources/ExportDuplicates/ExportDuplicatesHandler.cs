namespace Sankore.Modules.Leads.Features.LeadSources.ExportDuplicates;

using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ExportDuplicatesHandler(LeadsDbContext db)
    : IRequestHandler<ExportDuplicatesQuery, Result<ExportDuplicatesResult>>
{
    public async Task<Result<ExportDuplicatesResult>> Handle(
        ExportDuplicatesQuery query, CancellationToken ct)
    {
        var sourceExists = await db.LeadSourceConfigs
            .AnyAsync(s => s.Id == query.SourceId, ct);

        if (!sourceExists)
            return Result.Fail<ExportDuplicatesResult>("SOURCE_NOT_FOUND");

        // Only duplicate ingestions — no phone, no name (PII safety)
        var duplicates = await db.LeadIngestions
            .Where(i => i.SourceId == query.SourceId
                     && i.Status == LeadIngestionStatus.Duplicate)
            .OrderByDescending(i => i.IngestedAt)
            .Select(i => new { i.ExternalId, i.IngestedAt, i.LeadId })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("external_id,date,existing_lead_id");

        foreach (var d in duplicates)
        {
            sb.AppendLine($"{Escape(d.ExternalId)},{d.IngestedAt:yyyy-MM-dd HH:mm:ss},{d.LeadId}");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"duplicates-{query.SourceId.ToString()[..8]}.csv";

        return Result.Ok(new ExportDuplicatesResult(bytes, fileName));
    }

    private static string Escape(string? value)
    {
        if (value is null) return "";
        if (value.Contains(',') || value.Contains('"'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
