namespace Sankore.Modules.Leads.Features.Settings.GetLeadSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadSettingHandler(LeadsDbContext db)
    : IRequestHandler<GetLeadSettingQuery, Result<LeadSettingDto>>
{
    public async Task<Result<LeadSettingDto>> Handle(GetLeadSettingQuery query, CancellationToken ct)
    {
        var declared = LeadSettingKeys.Find(query.Key);
        if (declared is null)
            return Result.Fail<LeadSettingDto>(LeadSettingErrors.Unknown);

        // declared.Key, not query.Key: the catalogue lookup is case-insensitive, the column
        // comparison in PostgreSQL is not.
        var stored = await db.LeadSettings.FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        return Result.Ok(stored is null
            ? LeadSettingDto.FromDefault(declared)
            : LeadSettingDto.FromStored(stored, declared));
    }
}
