namespace Sankore.Modules.Leads.Features.ExportLeads;

using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ExportLeadsHandler(LeadsDbContext db)
    : IRequestHandler<ExportLeadsQuery, Result<byte[]>>
{
    private static readonly string[] Headers =
    [
        "Id", "FullName", "FirstName", "LastName", "PhoneNumber", "Email",
        "Gender", "DateOfBirth", "Status", "PipelineStage", "Source", "Channel",
        "Campaign", "ExternalReference", "InterestedProduct", "PreferredLanguage",
        "Score", "IntentLevel", "QualificationCompleteness",
        "OwnerId", "AgencyId", "CapturedAt", "ConvertedAt", "LossReason",
        // The configured source's Code, not its id: a bare uuid is unusable to whoever opens
        // this file in a spreadsheet. Appended last so existing column positions do not move.
        "SourceCode"
    ];

    public async Task<Result<byte[]>> Handle(ExportLeadsQuery query, CancellationToken ct)
    {
        var q = db.Leads.AsNoTracking();

        if (query.Status.HasValue)
            q = q.Where(l => l.Status == query.Status.Value);

        if (query.PipelineStage.HasValue)
            q = q.Where(l => l.PipelineStage == query.PipelineStage.Value);

        if (query.Source.HasValue)
            q = q.Where(l => l.Source == query.Source.Value);

        if (query.OwnerId.HasValue)
            q = q.Where(l => l.OwnerId == query.OwnerId.Value);

        if (query.AgencyId.HasValue)
            q = q.Where(l => l.AgencyId == query.AgencyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            q = q.Where(l =>
                l.FullName.ToLower().Contains(search) ||
                l.PhoneNumber.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().ToLowerInvariant();
            q = q.Where(l => db.LeadTags.Any(t => t.LeadId == l.Id && t.Tag == tag));
        }

        // Same filter as ListLeads, so exporting a filtered list exports what is on screen.
        if (query.LeadSourceConfigId.HasValue)
            q = q.Where(l => l.LeadSourceConfigId == query.LeadSourceConfigId.Value);

        var leads = await q
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        // One extra read for the whole file. Empty for leads with no configured source, and for
        // an id that no longer resolves — the reference is opaque, with no foreign key.
        var sourceIds = leads
            .Where(l => l.LeadSourceConfigId.HasValue)
            .Select(l => l.LeadSourceConfigId!.Value)
            .Distinct()
            .ToList();

        var sourceCodes = sourceIds.Count == 0
            ? []
            : await db.LeadSourceConfigs
                .AsNoTracking()
                .Where(s => sourceIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, ct);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Headers));

        foreach (var l in leads)
        {
            csv.AppendLine(string.Join(",",
                Escape(l.Id.ToString()),
                Escape(l.FullName),
                Escape(l.FirstName),
                Escape(l.LastName),
                Escape(l.PhoneNumber),
                Escape(l.Email),
                Escape(l.Gender.ToString()),
                Escape(l.DateOfBirth?.ToString("yyyy-MM-dd")),
                Escape(l.Status.ToString()),
                Escape(l.PipelineStage.ToString()),
                Escape(l.Source.ToString()),
                Escape(l.Channel?.ToString()),
                Escape(l.Campaign),
                Escape(l.ExternalReference),
                Escape(l.InterestedProduct),
                Escape(l.PreferredLanguage),
                l.Score.ToString(),
                Escape(l.IntentLevel.ToString()),
                l.QualificationCompleteness.ToString("F2"),
                Escape(l.OwnerId?.ToString()),
                Escape(l.AgencyId?.ToString()),
                Escape(l.CapturedAt.ToString("u")),
                Escape(l.ConvertedAt?.ToString("u")),
                Escape(l.LossReason),
                Escape(l.LeadSourceConfigId is { } sid && sourceCodes.TryGetValue(sid, out var code)
                    ? code
                    : null)));
        }

        return Result.Ok(Encoding.UTF8.GetBytes(csv.ToString()));
    }

    private static string Escape(string? value)
    {
        if (value is null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
