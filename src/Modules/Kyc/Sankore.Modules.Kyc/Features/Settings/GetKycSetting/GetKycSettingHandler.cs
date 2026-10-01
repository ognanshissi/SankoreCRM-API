namespace Sankore.Modules.Kyc.Features.Settings.GetKycSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetKycSettingHandler(KycDbContext db)
    : IRequestHandler<GetKycSettingQuery, Result<KycSettingDto>>
{
    public async Task<Result<KycSettingDto>> Handle(GetKycSettingQuery query, CancellationToken ct)
    {
        var declared = KycSettingKeys.Find(query.Key);
        if (declared is null)
            return Result.Fail<KycSettingDto>(KycErrors.SettingUnknown);

        // declared.Key, not query.Key: the catalogue lookup is case-insensitive, the column
        // comparison in PostgreSQL is not.
        var stored = await db.KycSettings.FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        return Result.Ok(stored is null
            ? KycSettingDto.FromDefault(declared)
            : KycSettingDto.FromStored(stored, declared));
    }
}
