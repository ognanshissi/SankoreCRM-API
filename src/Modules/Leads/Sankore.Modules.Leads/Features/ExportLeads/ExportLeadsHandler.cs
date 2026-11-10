namespace Sankore.Modules.Leads.Features.ExportLeads;

using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed class ExportLeadsHandler(
    LeadsDbContext db,
    LeadVisibility? visibility = null,
    Sankore.Modules.Administration.PublicApi.IAdministrationModule? administration = null,
    ITenantContext? tenant = null)
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
        "SourceCode",
        // The company (key accounts lot 1), under the import's column names so a file exported
        // here imports back as is. Appended for the same reason as SourceCode.
        "ProspectType", "CompanyName", "CompanyEmail", "CompanyPhone", "Website",
        "ActivitySector", "CompanySize", "EstimatedAnnualPurchases", "ContactJobTitle",
    ];

    public async Task<Result<byte[]>> Handle(ExportLeadsQuery query, CancellationToken ct)
    {
        var q = visibility?.Apply(db.Leads.AsNoTracking()) ?? db.Leads.AsNoTracking();

        // Same filters as ListLeads, so exporting a filtered list exports what is on screen.
        var filtered = await new LeadListFilter(
                query.Status, query.PipelineStage, query.Source, query.OwnerId, query.AgencyId, query.Search, query.Tag,
                query.IntentLevel, query.LeadSourceConfigId, query.AwaitingDispatch, query.CustomFilters, query.AgentId,
                query.ProspectType, query.ActivitySector, query.CompanySize, query.Potential)
            .ApplyAsync(q, db, administration, tenant, ct);
        if (filtered.IsFailure)
            return Result.Fail<byte[]>(filtered.Error!);

        var leads = await filtered.Value
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
                    : null),
                Escape(l.ProspectType.ToString()),
                Escape(l.CompanyName),
                Escape(l.CompanyEmail),
                Escape(l.CompanyPhone),
                Escape(l.Website),
                Escape(l.ActivitySectorCode),
                Escape(l.CompanySize?.ToString()),
                Escape(l.EstimatedAnnualPurchases?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Escape(l.ContactJobTitle)));
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
