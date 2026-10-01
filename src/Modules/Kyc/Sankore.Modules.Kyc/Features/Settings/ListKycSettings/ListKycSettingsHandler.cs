namespace Sankore.Modules.Kyc.Features.Settings.ListKycSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListKycSettingsHandler(KycDbContext db)
    : IRequestHandler<ListKycSettingsQuery, Result<IReadOnlyList<KycSettingDto>>>
{
    public async Task<Result<IReadOnlyList<KycSettingDto>>> Handle(
        ListKycSettingsQuery query, CancellationToken ct)
    {
        // The tenant query filter does the isolation: this read never sees another tenant's rows.
        var stored = await db.KycSettings
            .ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);

        // Driven by the CATALOGUE, not by the table. A key the seeder has not written yet still
        // appears, with the value every reader actually resolves to; and a stale row for a key that
        // no longer exists is not exposed, so a screen cannot offer to edit something nothing reads.
        var items = KycSettingKeys.Defaults
            .Select(declared => stored.TryGetValue(declared.Key, out var row)
                ? KycSettingDto.FromStored(row, declared)
                : KycSettingDto.FromDefault(declared))
            .ToList();

        return Result.Ok<IReadOnlyList<KycSettingDto>>(items);
    }
}
