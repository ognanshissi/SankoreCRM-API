namespace Sankore.Modules.Leads.Features.Settings.ListLeadSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListLeadSettingsHandler(LeadsDbContext db)
    : IRequestHandler<ListLeadSettingsQuery, Result<IReadOnlyList<LeadSettingDto>>>
{
    public async Task<Result<IReadOnlyList<LeadSettingDto>>> Handle(
        ListLeadSettingsQuery query, CancellationToken ct)
    {
        // The tenant query filter does the isolation: this read never sees another tenant's rows.
        var stored = await db.LeadSettings
            .ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);

        // Driven by the CATALOGUE, not by the table. A key the seeder has not written yet still
        // appears, with the value every reader actually resolves to; and a stale row for a key
        // that no longer exists is not exposed, so a screen cannot offer to edit something
        // nothing reads.
        var items = LeadSettingKeys.Defaults
            .Select(declared => stored.TryGetValue(declared.Key, out var row)
                ? LeadSettingDto.FromStored(row, declared)
                : LeadSettingDto.FromDefault(declared))
            .ToList();

        return Result.Ok<IReadOnlyList<LeadSettingDto>>(items);
    }
}
